// =============================================================================
//  WriteFileTests — covers write_file: create, overwrite, parent-dir creation,
//  size cap, error cases.
// =============================================================================

using ClaudeChat.Tools;

namespace ClaudeChat.Tests;

[Trait("Category", "Unit")]
public sealed class WriteFileTests : IDisposable
{
    private readonly string _tmp;

    public WriteFileTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "claudechat-writefile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Creates_new_file_with_contents()
    {
        var path = Path.Combine(_tmp, "new.txt");

        var result = WriteFile.Run(path, "hello, world");

        Assert.Contains("wrote", result);
        Assert.Contains("12 bytes", result);
        Assert.Equal("hello, world", File.ReadAllText(path));
    }

    [Fact]
    public void Overwrites_existing_file_without_warning()
    {
        var path = Path.Combine(_tmp, "exists.txt");
        File.WriteAllText(path, "old contents");

        var result = WriteFile.Run(path, "new contents");

        Assert.Contains("wrote", result);
        Assert.Equal("new contents", File.ReadAllText(path));
    }

    [Fact]
    public void Creates_missing_parent_directories()
    {
        var path = Path.Combine(_tmp, "deep", "nested", "structure", "file.txt");

        var result = WriteFile.Run(path, "x");

        Assert.Contains("wrote", result);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Returns_error_when_contents_exceed_cap()
    {
        var path = Path.Combine(_tmp, "big.txt");
        var oversize = new string('a', 100_001);

        var result = WriteFile.Run(path, oversize);

        Assert.StartsWith("error:", result);
        Assert.Contains("exceeds", result);
        Assert.False(File.Exists(path));   // nothing written
    }

    [Fact]
    public void Accepts_contents_at_cap_boundary()
    {
        var path = Path.Combine(_tmp, "exact.txt");
        var atLimit = new string('a', 100_000);

        var result = WriteFile.Run(path, atLimit);

        Assert.Contains("wrote", result);
        Assert.Equal(100_000, new FileInfo(path).Length);
    }

    [Fact]
    public void Returns_error_for_empty_path()
    {
        var result = WriteFile.Run("", "anything");

        Assert.StartsWith("error:", result);
        Assert.Contains("path", result);
    }

    [Fact]
    public void Reports_byte_count_for_multibyte_utf8()
    {
        var path = Path.Combine(_tmp, "utf8.txt");
        // "héllo" — h(1) + é(2) + l(1) + l(1) + o(1) = 6 bytes in UTF-8
        var result = WriteFile.Run(path, "héllo");

        Assert.Contains("6 bytes", result);
    }
}
