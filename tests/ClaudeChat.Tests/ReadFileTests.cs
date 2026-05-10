// =============================================================================
//  ReadFileTests — covers the read_file tool: happy path, missing file, oversize.
//
//  Pure: no API key, no agent, no MAF — just exercises the tool method directly.
//  ReadFile.Read is a static function over the filesystem, so each test writes
//  its own scratch file in a unique temp directory and asserts against the
//  return string.
// =============================================================================

using ClaudeChat.Tools;

namespace ClaudeChat.Tests;

[Trait("Category", "Unit")]
public sealed class ReadFileTests : IDisposable
{
    private readonly string _tmp;

    public ReadFileTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "claudechat-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Returns_contents_of_existing_file()
    {
        var path = Path.Combine(_tmp, "hello.txt");
        File.WriteAllText(path, "hello, world");

        var result = ReadFile.Read(path);

        Assert.Equal("hello, world", result);
    }

    [Fact]
    public void Returns_error_for_missing_file()
    {
        var path = Path.Combine(_tmp, "nope.txt");

        var result = ReadFile.Read(path);

        Assert.StartsWith("error:", result);
        Assert.Contains("no file at", result);
        Assert.Contains(path, result);
    }

    [Fact]
    public void Returns_error_for_oversized_file()
    {
        var path = Path.Combine(_tmp, "big.bin");
        // 100_001 bytes — one past the 100KB limit.
        File.WriteAllBytes(path, new byte[100_001]);

        var result = ReadFile.Read(path);

        Assert.StartsWith("error:", result);
        Assert.Contains("exceeds", result);
    }

    [Fact]
    public void Reads_at_size_limit_boundary()
    {
        var path = Path.Combine(_tmp, "exactly_100k.bin");
        var content = new string('a', 100_000);
        File.WriteAllText(path, content);

        var result = ReadFile.Read(path);

        // Exactly at the cap should succeed (limit is "> MaxBytes", not ">=").
        Assert.Equal(content, result);
    }
}
