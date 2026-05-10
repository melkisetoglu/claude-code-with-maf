// =============================================================================
//  GrepTests — covers grep tool: single-file search, recursive search,
//  .git pruning, binary skip, no-match, invalid regex, missing path.
// =============================================================================

using ClaudeChat.Tools;

namespace ClaudeChat.Tests;

[Trait("Category", "Unit")]
public sealed class GrepTests : IDisposable
{
    private readonly string _tmp;

    public GrepTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "claudechat-grep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Finds_pattern_in_single_file()
    {
        var path = Path.Combine(_tmp, "a.txt");
        File.WriteAllText(path, "first line\nsecond TARGET line\nthird line");

        var output = Grep.Run("TARGET", path);

        Assert.Contains($"{path}:2:", output);
        Assert.Contains("second TARGET line", output);
    }

    [Fact]
    public void Recursive_search_finds_matches_across_files()
    {
        File.WriteAllText(Path.Combine(_tmp, "a.txt"), "alpha NEEDLE bravo");
        Directory.CreateDirectory(Path.Combine(_tmp, "sub"));
        File.WriteAllText(Path.Combine(_tmp, "sub", "b.txt"), "x\ny NEEDLE z");

        var output = Grep.Run("NEEDLE", _tmp);

        Assert.Contains("a.txt:1:", output);
        Assert.Contains("b.txt:2:", output);
    }

    [Fact]
    public void Recursive_search_skips_dot_git()
    {
        File.WriteAllText(Path.Combine(_tmp, "a.txt"), "NEEDLE");
        var gitDir = Path.Combine(_tmp, ".git");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "object"), "NEEDLE");

        var output = Grep.Run("NEEDLE", _tmp);

        Assert.Contains("a.txt:1:", output);
        Assert.DoesNotContain(".git", output);
    }

    [Fact]
    public void Skips_binary_looking_files()
    {
        File.WriteAllText(Path.Combine(_tmp, "text.txt"), "hello NEEDLE world");
        // Binary file: NUL bytes early.
        File.WriteAllBytes(Path.Combine(_tmp, "blob.bin"),
            new byte[] { 0x00, 0x4E, 0x45, 0x45, 0x44, 0x4C, 0x45 });   // \0NEEDLE

        var output = Grep.Run("NEEDLE", _tmp);

        Assert.Contains("text.txt", output);
        Assert.DoesNotContain("blob.bin", output);
    }

    [Fact]
    public void Returns_no_matches_when_nothing_matches()
    {
        File.WriteAllText(Path.Combine(_tmp, "a.txt"), "nothing here");

        var output = Grep.Run("MISSING", _tmp);

        Assert.Equal("no matches", output);
    }

    [Fact]
    public void Returns_error_for_invalid_regex()
    {
        var output = Grep.Run("[invalid(", _tmp);

        Assert.StartsWith("error:", output);
        Assert.Contains("invalid regex", output);
    }

    [Fact]
    public void Returns_error_for_missing_path()
    {
        var missing = Path.Combine(_tmp, "nope");

        var output = Grep.Run("anything", missing);

        Assert.StartsWith("error:", output);
        Assert.Contains(missing, output);
    }

    [Fact]
    public void Returns_error_for_empty_pattern()
    {
        var output = Grep.Run("", _tmp);

        Assert.StartsWith("error:", output);
        Assert.Contains("pattern", output);
    }
}
