using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using emMDee.Models;
using emMDee.Services;
using Xunit;

namespace emMDee.Tests;

/// <summary>
/// The stateful half of the update check: how often it is allowed to call out,
/// what it does when the network fails, and whether "Dismiss" actually sticks.
/// These are the behaviours a user would notice if they regressed — a banner
/// that returns every launch, or a check that silently stops happening.
/// </summary>
public sealed class UpdateCheckServiceTests : IDisposable
{
    private readonly string _statePath;

    public UpdateCheckServiceTests()
    {
        _statePath = Path.Combine(
            Path.GetTempPath(), "emMDee-update-" + Guid.NewGuid().ToString("N"), "update-check.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_statePath)!, recursive: true); } catch { }
    }

    private static string Payload(string tag) => $$"""
        { "tag_name": "{{tag}}", "html_url": "https://github.com/foodak/emMDee/releases/tag/{{tag}}" }
        """;

    private UpdateCheckService NewService(StubHandler handler) => new(new HttpClient(handler), _statePath);

    /// <summary>Backdates the recorded check so the daily throttle has expired.</summary>
    private void AgeThrottle()
    {
        var state = JsonNode.Parse(File.ReadAllText(_statePath))!.AsObject();
        state["LastCheckedUtc"] = DateTimeOffset.UtcNow.AddHours(-25);
        File.WriteAllText(_statePath, state.ToJsonString());
    }

    [Fact]
    public async Task Reports_a_newer_release()
    {
        var service = NewService(StubHandler.Responding(HttpStatusCode.OK, Payload("v9.9.9")));

        var result = await service.CheckForUpdateAsync("1.0.5");

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
        Assert.Equal("9.9.9", result.Release!.Version);
        Assert.Equal("v9.9.9", result.Release.TagName);
    }

    [Fact]
    public async Task Reports_up_to_date_when_running_the_newest_version()
    {
        var service = NewService(StubHandler.Responding(HttpStatusCode.OK, Payload("v1.0.5")));

        var result = await service.CheckForUpdateAsync("1.0.5");

        Assert.Equal(UpdateCheckOutcome.UpToDate, result.Outcome);
    }

    [Fact]
    public async Task A_dismissed_version_stays_dismissed_across_restarts()
    {
        var first = NewService(StubHandler.Responding(HttpStatusCode.OK, Payload("v1.0.6")));
        var found = await first.CheckForUpdateAsync("1.0.5");
        first.Dismiss(found.Release!);

        // New process, same state file: no network call should even be attempted
        // for the version the user already waved away.
        var restarted = NewService(StubHandler.Failing());
        var result = await restarted.CheckForUpdateAsync("1.0.5");

        Assert.Equal(UpdateCheckOutcome.Skipped, result.Outcome);
    }

    [Fact]
    public async Task A_later_release_is_still_reported_after_dismissing_an_earlier_one()
    {
        var first = NewService(StubHandler.Responding(HttpStatusCode.OK, Payload("v1.0.6")));
        first.Dismiss((await first.CheckForUpdateAsync("1.0.5")).Release!);

        // Dismissal suppresses one version, not the feature — but the daily
        // throttle still applies, so this has to be the next day for a new
        // release to be noticed.
        AgeThrottle();

        var later = NewService(StubHandler.Responding(HttpStatusCode.OK, Payload("v1.0.7")));
        var result = await later.CheckForUpdateAsync("1.0.5");

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
        Assert.Equal("1.0.7", result.Release!.Version);
    }

    [Fact]
    public async Task A_forced_check_overrides_an_earlier_dismissal()
    {
        var service = NewService(StubHandler.Responding(HttpStatusCode.OK, Payload("v1.0.6")));
        var found = await service.CheckForUpdateAsync("1.0.5");
        service.Dismiss(found.Release!);

        var result = await service.CheckForUpdateAsync("1.0.5", force: true);

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
    }

    [Fact]
    public async Task A_second_check_the_same_day_does_not_call_github_again()
    {
        var handler = StubHandler.Responding(HttpStatusCode.OK, Payload("v1.0.6"));
        var service = NewService(handler);
        await service.CheckForUpdateAsync("1.0.5");
        Assert.Equal(1, handler.Requests);

        var result = await service.CheckForUpdateAsync("1.0.5");

        Assert.Equal(1, handler.Requests);                       // throttled
        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);   // but still known
    }

    [Fact]
    public async Task An_offline_check_is_silent_when_nothing_is_known()
    {
        var service = NewService(StubHandler.Failing());

        var result = await service.CheckForUpdateAsync("1.0.5");

        Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
        Assert.Null(result.Release);
    }

    [Fact]
    public async Task An_offline_check_still_reports_a_previously_seen_release()
    {
        var service = NewService(StubHandler.Responding(HttpStatusCode.OK, Payload("v1.0.6")));
        await service.CheckForUpdateAsync("1.0.5");

        // Next day, and GitHub is unreachable: yesterday's answer still stands.
        AgeThrottle();
        var offline = NewService(StubHandler.Failing());
        var result = await offline.CheckForUpdateAsync("1.0.5");

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
        Assert.Equal("1.0.6", result.Release!.Version);
    }

    [Fact]
    public async Task A_rate_limited_response_fails_quietly_without_advancing_the_throttle()
    {
        var handler = StubHandler.Responding((HttpStatusCode)403, """{ "message": "rate limited" }""");
        var service = NewService(handler);

        var first = await service.CheckForUpdateAsync("1.0.5");
        var second = await service.CheckForUpdateAsync("1.0.5");

        Assert.Equal(UpdateCheckOutcome.Failed, first.Outcome);
        Assert.Equal(2, handler.Requests);   // not throttled: we learned nothing
        Assert.Equal(UpdateCheckOutcome.Failed, second.Outcome);
    }

    [Fact]
    public async Task A_malformed_payload_fails_quietly()
    {
        var service = NewService(StubHandler.Responding(HttpStatusCode.OK, "not json"));

        var result = await service.CheckForUpdateAsync("1.0.5", force: true);

        Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
    }

    /// <summary>Stands in for the network: scripted response, no sockets.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage>? _respond;
        private readonly bool _throw;

        private StubHandler(Func<HttpResponseMessage>? respond, bool throwing)
        {
            _respond = respond;
            _throw = throwing;
        }

        public int Requests { get; private set; }

        public static StubHandler Responding(HttpStatusCode status, string body)
            => new(() => new HttpResponseMessage(status) { Content = new StringContent(body) }, throwing: false);

        public static StubHandler Failing() => new(null, throwing: true);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (_throw)
                throw new HttpRequestException("simulated offline");

            return Task.FromResult(_respond!());
        }
    }
}
