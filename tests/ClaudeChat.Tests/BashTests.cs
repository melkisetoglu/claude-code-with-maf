// =============================================================================
//  BashTests — covers bash: happy path, exit codes, stderr capture, timeout,
//  output cap, missing cwd, empty command.
//
//  These tests actually invoke /bin/bash. They run on macOS and Linux; the
//  workshop is not Windows-friendly here. Each test uses a unique temp dir
//  for isolation.
// =============================================================================

using ClaudeChat.Tools;

namespace ClaudeChat.Tests;

[Trait("Category", "Unit")]
public sealed class BashTests : IDisposable
{
    private readonly string _tmp;

    public BashTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "claudechat-bash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Captures_stdout()
    {
        var result = Bash.Run("echo hello world", _tmp);

        Assert.Equal("hello world", result);
    }

    [Fact]
    public void Captures_stderr()
    {
        var result = Bash.Run("echo on-stderr 1>&2", _tmp);

        Assert.Contains("on-stderr", result);
    }

    [Fact]
    public void Reports_nonzero_exit_codes()
    {
        var result = Bash.Run("exit 42", _tmp);

        Assert.Contains("exit code 42", result);
    }

    [Fact]
    public void Reports_no_output_when_command_writes_nothing()
    {
        var result = Bash.Run("true", _tmp);

        Assert.Equal("(no output)", result);
    }

    [Fact]
    public void Honors_working_directory()
    {
        File.WriteAllText(Path.Combine(_tmp, "marker.txt"), "");

        var result = Bash.Run("ls marker.txt", _tmp);

        Assert.Contains("marker.txt", result);
    }

    [Fact]
    public void Returns_error_for_missing_cwd()
    {
        var missing = Path.Combine(_tmp, "nope");

        var result = Bash.Run("echo hi", missing);

        Assert.StartsWith("error:", result);
    }

    [Fact]
    public void Returns_error_for_empty_command()
    {
        var result = Bash.Run("", _tmp);

        Assert.StartsWith("error:", result);
        Assert.Contains("command", result);
    }

    [Fact(Timeout = 35_000)]
    public async Task Times_out_long_running_commands()
    {
        // sleep 60 should be killed after Bash's internal 30s timeout; the
        // 35s xUnit timeout is a safety belt in case the internal timeout
        // misbehaves so a test hang doesn't lock the suite.
        var result = await Task.Run(() => Bash.Run("sleep 60", _tmp));

        Assert.Contains("timed out", result);
    }

    [Fact]
    public void Truncates_oversized_output()
    {
        // Generate ~60KB of output to exceed the 50KB cap.
        var result = Bash.Run("yes a | head -c 60000", _tmp);

        Assert.Contains("truncated", result);
    }
}
