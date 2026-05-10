// =============================================================================
//  GlobTests — covers glob tool: top-level vs recursive, .git exclusion,
//  no-match, missing-root, missing pattern.
// =============================================================================

using ClaudeChat.Tools;

namespace ClaudeChat.Tests;

[Trait("Category", "Unit")]
public sealed class GlobTests : IDisposable
{
    private readonly string _tmp;

    public GlobTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "claudechat-glob-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Recursive_pattern_matches_nested_files()
    {
        File.WriteAllText(Path.Combine(_tmp, "top.cs"), "");
        Directory.CreateDirectory(Path.Combine(_tmp, "src"));
        File.WriteAllText(Path.Combine(_tmp, "src", "deep.cs"), "");

        var output = Glob.Run("**/*.cs", _tmp);

        Assert.Contains("top.cs", output);
        Assert.Contains("deep.cs", output);
    }

    [Fact]
    public void Toplevel_only_pattern_excludes_nested()
    {
        File.WriteAllText(Path.Combine(_tmp, "top.cs"), "");
        Directory.CreateDirectory(Path.Combine(_tmp, "src"));
        File.WriteAllText(Path.Combine(_tmp, "src", "deep.cs"), "");

        var output = Glob.Run("*.cs", _tmp);

        Assert.Contains("top.cs", output);
        Assert.DoesNotContain("deep.cs", output);
    }

    [Fact]
    public void Excludes_dot_git_directory()
    {
        File.WriteAllText(Path.Combine(_tmp, "real.cs"), "");
        var gitDir = Path.Combine(_tmp, ".git");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "object.cs"), "");

        var output = Glob.Run("**/*.cs", _tmp);

        Assert.Contains("real.cs", output);
        Assert.DoesNotContain("object.cs", output);
    }

    [Fact]
    public void Returns_no_matches_when_nothing_matches()
    {
        File.WriteAllText(Path.Combine(_tmp, "thing.txt"), "");

        var output = Glob.Run("**/*.cs", _tmp);

        Assert.Equal("no matches", output);
    }

    [Fact]
    public void Returns_error_for_missing_root()
    {
        var missing = Path.Combine(_tmp, "nope");

        var output = Glob.Run("*.cs", missing);

        Assert.StartsWith("error:", output);
        Assert.Contains(missing, output);
    }

    [Fact]
    public void Returns_error_for_empty_pattern()
    {
        var output = Glob.Run("", _tmp);

        Assert.StartsWith("error:", output);
        Assert.Contains("pattern", output);
    }
}
