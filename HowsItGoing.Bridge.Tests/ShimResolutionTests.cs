using FluentAssertions;
using HowsItGoing.Bridge.Services;
using NUnit.Framework;

namespace HowsItGoing.Bridge.Tests;

/// <summary>
/// The bridge must never hand an agent launch to cmd.exe: .NET quotes arguments with backslash
/// escaping, which cmd.exe does not honour, so a quote in a request value could break out of the
/// quoting and run a second command after an unquoted '&amp;'.
/// </summary>
[TestFixture]
public sealed class ShimResolutionTests
{
    private string _root = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "hig-shim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory must not fail the run.
        }
    }

    /// <summary>Mirrors the shim npm generates on Windows.</summary>
    private string WriteNpmShim(string toolName, string relativeTarget)
    {
        var shimPath = Path.Combine(_root, toolName + ".cmd");
        File.WriteAllText(shimPath, string.Join(Environment.NewLine,
        [
            "@ECHO off",
            "GOTO start",
            ":find_dp0",
            "SET dp0=%~dp0",
            "EXIT /b",
            ":start",
            "SETLOCAL",
            "CALL :find_dp0",
            $"\"%dp0%\\{relativeTarget}\"   %*"
        ]));

        var targetPath = Path.Combine(_root, relativeTarget.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.WriteAllText(targetPath, string.Empty);
        return shimPath;
    }

    [Test]
    [Platform("Win")]
    public void TryResolveTarget_reads_the_real_executable_out_of_an_npm_shim()
    {
        var shimPath = WriteNpmShim("opencode", @"node_modules\opencode-ai\bin\opencode.exe");

        var resolved = WindowsShimResolver.TryResolveTarget(shimPath);

        resolved.Should().Be(Path.Combine(_root, "node_modules", "opencode-ai", "bin", "opencode.exe"));
    }

    [Test]
    [Platform("Win")]
    public void FromExecutablePath_launches_the_shim_target_directly_rather_than_through_cmd()
    {
        var shimPath = WriteNpmShim("claude", @"node_modules\@anthropic-ai\claude-code\bin\claude.exe");

        var spec = LaunchSpecFactory.FromExecutablePath(shimPath);

        spec.Mode.Should().Be(CodexLaunchMode.Direct);
        spec.FileName.Should().Be(Path.Combine(_root, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe"));
        spec.FileName.Should().NotContain("cmd.exe");
    }

    [Test]
    [Platform("Win")]
    public void FromExecutablePath_fails_closed_when_a_shim_cannot_be_resolved()
    {
        var shimPath = Path.Combine(_root, "mystery.cmd");
        File.WriteAllText(shimPath, "@echo off" + Environment.NewLine + "echo nothing to see here");

        FluentActions.Invoking(() => LaunchSpecFactory.FromExecutablePath(shimPath))
            .Should().Throw<FileNotFoundException>()
            .WithMessage("*cmd.exe*");
    }

    [Test]
    [Platform("Win")]
    public void CreateStartInfo_never_targets_a_shell()
    {
        var shimPath = WriteNpmShim("opencode", @"node_modules\opencode-ai\bin\opencode.exe");
        var spec = LaunchSpecFactory.FromExecutablePath(shimPath);

        // The message that previously escaped cmd.exe quoting.
        var hostileMessage = "hello\"&echo PWNED&echo \"";
        var startInfo = AgentProcessFactory.CreateStartInfo(_root, spec, ["run", "-s", "sess1", "--auto", hostileMessage]);

        startInfo.FileName.Should().EndWith("opencode.exe");
        startInfo.ArgumentList.Should().Contain(hostileMessage);
        // Passed as one argv element, so there is no command line for cmd.exe to re-split.
        startInfo.ArgumentList.Count(argument => argument.Contains("PWNED")).Should().Be(1);
    }
}
