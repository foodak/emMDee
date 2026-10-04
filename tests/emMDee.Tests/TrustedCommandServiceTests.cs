using System.IO;
using emMDee.Services;
using Xunit;

namespace emMDee.Tests;

/// <summary>
/// Roundtrips through the store that matter to the owner: a wrapper for an
/// approved program runs without asking; the same wrapper stops as soon as the
/// program changes, the wrapper smuggles environment, or the wrapper stops
/// being a provable forwarder.
/// </summary>
public sealed class TrustedCommandServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _storePath;
    private readonly string _devCmd;
    private readonly string _otherDir;

    public TrustedCommandServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "emMDee-trust-" + Guid.NewGuid().ToString("N"));
        _otherDir = Path.Combine(Path.GetTempPath(), "emMDee-trust-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_otherDir);
        _storePath = Path.Combine(_dir, "trusted-commands.json");
        _devCmd = Path.Combine(_dir, "SkyRun", "dev.cmd");
        Directory.CreateDirectory(Path.GetDirectoryName(_devCmd)!);
        File.WriteAllText(_devCmd, "@echo off\r\necho the program\r\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        try { Directory.Delete(_otherDir, recursive: true); } catch { }
    }

    private string WriteWrapper(string name, string content, string? folder = null)
    {
        var path = Path.Combine(folder ?? _otherDir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private const string GateVars = """
        set SKYRUN_GATE_TIMEOUT_S=20
        set SKYRUN_NO_GATE_LOCK=1
        """;

    private string WrapperFor(string target, string extra = "")
    {
        // Guarantee each extra fragment ends on its own line, so a fragment
        // without a trailing newline cannot glue the `call` onto it.
        var prefix = extra.Length > 0 && !extra.EndsWith('\n') ? extra + "\r\n" : extra;
        return "@echo off\r\n"
            + prefix
            + $"call \"{target}\" run --world map2\r\n";
    }

    [Fact]
    public void WrapperForApprovedProgram_IsTrusted_FromAnyFolder()
    {
        var service = new TrustedCommandService(_storePath);
        service.Trust(_devCmd, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SKYRUN_GATE_TIMEOUT_S", "SKYRUN_NO_GATE_LOCK" });

        var wrapper = WriteWrapper("run-1.cmd", WrapperFor(_devCmd, GateVars));

        Assert.True(service.IsTrusted(wrapper));
    }

    [Fact]
    public void WrapperSettingUnrecordedVariable_AsksAgain()
    {
        var service = new TrustedCommandService(_storePath);
        service.Trust(_devCmd, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SKYRUN_GATE_TIMEOUT_S" });

        var wrapper = WriteWrapper("run-2.cmd", WrapperFor(_devCmd, GateVars));

        Assert.False(service.IsTrusted(wrapper));
    }

    [Fact]
    public void WrapperSettingSubsetOfRecordedVariables_IsTrusted()
    {
        var service = new TrustedCommandService(_storePath);
        service.Trust(_devCmd, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SKYRUN_GATE_TIMEOUT_S", "SKYRUN_NO_GATE_LOCK" });

        var wrapper = WriteWrapper("run-3.cmd", WrapperFor(_devCmd, "set SKYRUN_GATE_TIMEOUT_S=20\r\n"));

        Assert.True(service.IsTrusted(wrapper));
    }

    [Fact]
    public void ChangedProgramDigest_AsksAgain()
    {
        var service = new TrustedCommandService(_storePath);
        service.Trust(_devCmd, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SKYRUN_GATE_TIMEOUT_S" });

        File.WriteAllText(_devCmd, "@echo off\r\necho the program, now different\r\n");

        var wrapper = WriteWrapper("run-4.cmd", WrapperFor(_devCmd, "set SKYRUN_GATE_TIMEOUT_S=20\r\n"));
        Assert.False(service.IsTrusted(wrapper));
    }

    [Fact]
    public void NonForwarderWrapper_IsNeverCovered_ByTheProgramsItMentions()
    {
        var service = new TrustedCommandService(_storePath);
        service.Trust(_devCmd, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SKYRUN_GATE_TIMEOUT_S" });

        // Mentions dev.cmd but does other things too — the resolver cannot
        // prove it, so it asks.
        var wrapper = WriteWrapper("run-5.cmd", WrapperFor(_devCmd, "set SKYRUN_GATE_TIMEOUT_S=20\r\nstart evil.exe\r\n"));

        Assert.False(service.IsTrusted(wrapper));
    }

    [Fact]
    public void FolderApproval_OfTheProgramsFolder_CoversWrappers()
    {
        var service = new TrustedCommandService(_storePath);
        service.TrustFolder(_devCmd);

        var wrapper = WriteWrapper("run-6.cmd", WrapperFor(_devCmd, "set ANYTHING=1\r\n"));

        Assert.True(service.IsTrusted(wrapper));
    }

    [Fact]
    public void DirectFileApproval_StillWorks_ForTheFileItself()
    {
        var service = new TrustedCommandService(_storePath);
        var wrapper = WriteWrapper("run-7.cmd", WrapperFor(_devCmd, GateVars));
        service.Trust(wrapper);

        Assert.True(service.IsTrusted(wrapper));
    }

    [Fact]
    public void ApprovalsSurviveReload()
    {
        var first = new TrustedCommandService(_storePath);
        first.Trust(_devCmd, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SKYRUN_GATE_TIMEOUT_S", "SKYRUN_NO_GATE_LOCK" });

        var second = new TrustedCommandService(_storePath);
        var wrapper = WriteWrapper("run-8.cmd", WrapperFor(_devCmd, GateVars));

        Assert.True(second.IsTrusted(wrapper));
    }

    [Fact]
    public void LegacyRecordWithoutEnvVars_IsTreatedAsEmptyAllowlist()
    {
        // A record written before wrappers existed: same shape, no EnvVars.
        File.WriteAllText(_storePath,
            $"[{{\"Path\": \"{_devCmd.Replace("\\", "\\\\")}\", \"Sha256\": \"{DigestOf(_devCmd)}\", \"Kind\": \"file\", \"ApprovedUtc\": \"2026-09-05T00:00:00Z\"}}]");

        var service = new TrustedCommandService(_storePath);

        Assert.True(service.IsTrusted(_devCmd));                       // the file itself: unchanged
        var cleanWrapper = WriteWrapper("run-9.cmd", WrapperFor(_devCmd));
        Assert.True(service.IsTrusted(cleanWrapper));                  // sets no env: covered
        var envWrapper = WriteWrapper("run-10.cmd", WrapperFor(_devCmd, GateVars));
        Assert.False(service.IsTrusted(envWrapper));                   // sets env: asks once
    }

    private static string DigestOf(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }
}
