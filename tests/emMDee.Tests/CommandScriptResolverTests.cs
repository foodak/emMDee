using System.IO;
using emMDee.Services;
using Xunit;

namespace emMDee.Tests;

/// <summary>
/// The resolver gates a security prompt: it must accept exactly the wrapper
/// pattern it can prove, and reject everything it cannot. These tests pin the
/// real SkyRun wrapper template and the ways it must not be fooled.
/// </summary>
public sealed class CommandScriptResolverTests : IDisposable
{
    private readonly string _dir;
    private string _scriptPath;

    public CommandScriptResolverTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "emMDee-resolver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _scriptPath = Path.Combine(_dir, "wrapper.cmd");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private ResolvedForwarder? Resolve(string content)
    {
        File.WriteAllText(_scriptPath, content);
        return CommandScriptResolver.TryResolve(_scriptPath);
    }

    /// <summary>The SkyRun RUN IT template, verbatim in shape (paths shortened).</summary>
    private const string SkyRunTemplate = """
        @echo off

        if not exist "C:\Games\SkyRun\dev.cmd" (

          echo This review command expected the SkyRun checkout at "C:\Games\SkyRun",

          echo but there is no dev.cmd there now.

          echo Run this from your SkyRun folder instead:  dev.cmd run --world map2 --spawn-at=4.82,3.42,-6.87 --spawn-look=2.60,3.70,-3.60

          pause

          exit /b 1

        )

        cd /d "C:\Games\SkyRun"

        set SKYRUN_GATE_TIMEOUT_S=20

        call "C:\Games\SkyRun\dev.cmd" run --world map2 --spawn-at=4.82,3.42,-6.87 --spawn-look=2.60,3.70,-3.60

        if errorlevel 3 (

          if not errorlevel 4 (

            echo.

            echo An agent is measuring on this machine right now, which is why that

            echo took 20 seconds and stopped. You can go in anyway - their

            echo measurement gets re-run, nothing of yours is lost.

            echo.

            choice /c yn /n /m "Launch anyway? [y/n] "

            if errorlevel 2 exit /b 3

            set SKYRUN_NO_GATE_LOCK=1

            call "C:\Games\SkyRun\dev.cmd" run --world map2 --spawn-at=4.82,3.42,-6.87 --spawn-look=2.60,3.70,-3.60

          )

        )

        if errorlevel 1 pause
        """;

    [Fact]
    public void SkyRunTemplate_ResolvesToDevCmd_WithGateEnvVars()
    {
        var resolved = Resolve(SkyRunTemplate);

        Assert.NotNull(resolved);
        var targets = resolved.InvokedExecutables
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.Single(targets);
        Assert.Equal(@"C:\Games\SkyRun\dev.cmd", targets[0], ignoreCase: true);
        Assert.Equal(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SKYRUN_GATE_TIMEOUT_S", "SKYRUN_NO_GATE_LOCK" },
            resolved.EnvVarsSet);
    }

    [Theory]
    [InlineData("cmd")]
    [InlineData("BAT")]
    public void BatchExtensions_AreAcceptedCaseInsensitively(string extension)
    {
        _scriptPath = Path.Combine(_dir, "wrapper." + extension);
        var resolved = Resolve("call \"C:\\Games\\SkyRun\\dev.cmd\" run\r\n");
        Assert.NotNull(resolved);
    }

    [Fact]
    public void NonBatchExtension_IsNotResolved()
    {
        _scriptPath = Path.Combine(_dir, "wrapper.exe");
        var resolved = Resolve("call \"C:\\Games\\SkyRun\\dev.cmd\" run\r\n");
        Assert.Null(resolved);
    }

    [Fact]
    public void BenignLines_AreAccepted()
    {
        var resolved = Resolve("""
            @echo off
            rem a comment
            :: another comment
            echo anything at all
            pause
            exit /b 0
            cd /d "C:\Games\SkyRun"
            set SKYRUN_GATE_TIMEOUT_S=20
            choice /c yn /n /m "Go on? [y/n] "
            call "C:\Games\SkyRun\dev.cmd" run
            """);

        Assert.NotNull(resolved);
        Assert.Single(resolved.InvokedExecutables);
        Assert.Single(resolved.EnvVarsSet);
    }

    [Fact]
    public void ElseBranch_IsParsed()
    {
        var resolved = Resolve("""
            if exist "C:\Games\SkyRun\dev.cmd" (
              call "C:\Games\SkyRun\dev.cmd" run
            ) else (
              echo missing
              pause
            )
            """);

        Assert.NotNull(resolved);
        Assert.Single(resolved.InvokedExecutables);
    }

    [Fact]
    public void UnbalancedBlocks_AreRejected()
    {
        Assert.Null(Resolve("if exist \"C:\\Games\\SkyRun\\dev.cmd\" (\r\ncall \"C:\\Games\\SkyRun\\dev.cmd\" run\r\n"));
        Assert.Null(Resolve("call \"C:\\Games\\SkyRun\\dev.cmd\" run\r\n)\r\n"));
    }

    [Theory]
    [InlineData("echo done & evil.exe")]                       // chaining outside quotes
    [InlineData("call \"C:\\Games\\dev.cmd\" run > out.txt")]   // redirection
    [InlineData("call \"C:\\Games\\dev.cmd\" run | more")]      // pipe
    [InlineData("call \"C:\\Games\\dev.cmd\" run ^\r\n")]        // escape
    [InlineData("set X=%PATH%")]                               // % expansion
    [InlineData("echo !DELAYED!")]                             // ! expansion
    [InlineData("start evil.exe")]
    [InlineData("powershell -c evil")]
    [InlineData("cmd /c evil.exe")]
    [InlineData("for /f %i in ('evil.exe') do echo %i")]
    [InlineData("goto :elsewhere")]
    [InlineData(":label")]
    [InlineData("set /p NAME=what?")]
    [InlineData("set /a N=1+1")]
    [InlineData("set")]
    [InlineData("call C:\\Games\\dev.cmd run")]                // unquoted target
    [InlineData("call \"dev.cmd\" run")]                       // relative target
    [InlineData("call \"C:\\Games\\dev.cmd\" run \"a\"")]       // quoted argument
    [InlineData("evil.exe")]
    [InlineData("if defined X echo hi")]
    [InlineData("if exist \"x\" echo hi")]
    [InlineData("if errorlevel 1 ( echo hi )")]
    [InlineData("title spoofed")]
    [InlineData("setlocal")]
    [InlineData(") else echo nope")]
    public void UnknownOrDangerousLines_AreRejected(string line)
    {
        Assert.Null(Resolve(line));
    }

    [Fact]
    public void MultipleTargets_AreAllReported()
    {
        var resolved = Resolve("""
            call "C:\Games\SkyRun\dev.cmd" run
            call "C:\Games\SkyRun\godot.exe" --headless
            """);

        Assert.NotNull(resolved);
        Assert.Equal(2, resolved.InvokedExecutables.Count);
        Assert.Contains(resolved.InvokedExecutables, p => p.EndsWith("dev.cmd", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(resolved.InvokedExecutables, p => p.EndsWith("godot.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EmptyScript_ResolvesWithNoTargets()
    {
        var resolved = Resolve("");
        Assert.NotNull(resolved);
        Assert.Empty(resolved.InvokedExecutables);
    }
}
