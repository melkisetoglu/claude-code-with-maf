// =============================================================================
//  EditFileTests — covers edit_file: happy path, missing file, not-found,
//  ambiguity error, replace_all, error cases.
// =============================================================================

using ClaudeChat.Tools;

namespace ClaudeChat.Tests;

[Trait("Category", "Unit")]
public sealed class EditFileTests : IDisposable
{
    private readonly string _tmp;

    public EditFileTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "claudechat-editfile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Replaces_unique_occurrence()
    {
        var path = Path.Combine(_tmp, "a.txt");
        File.WriteAllText(path, "hello world");

        var result = EditFile.Run(path, "world", "there");

        Assert.Contains("replaced 1 occurrence", result);
        Assert.Equal("hello there", File.ReadAllText(path));
    }

    [Fact]
    public void Returns_error_when_file_missing()
    {
        var path = Path.Combine(_tmp, "nope.txt");

        var result = EditFile.Run(path, "a", "b");

        Assert.StartsWith("error:", result);
        Assert.Contains("no file", result);
    }

    [Fact]
    public void Returns_error_when_old_string_not_found()
    {
        var path = Path.Combine(_tmp, "a.txt");
        File.WriteAllText(path, "hello world");

        var result = EditFile.Run(path, "MISSING", "x");

        Assert.StartsWith("error:", result);
        Assert.Contains("not found", result);
        Assert.Equal("hello world", File.ReadAllText(path));   // file unchanged
    }

    [Fact]
    public void Returns_error_when_old_string_appears_multiple_times()
    {
        var path = Path.Combine(_tmp, "a.txt");
        File.WriteAllText(path, "foo bar foo baz foo");

        var result = EditFile.Run(path, "foo", "FOO");

        Assert.StartsWith("error:", result);
        Assert.Contains("appears 3 times", result);
        Assert.Equal("foo bar foo baz foo", File.ReadAllText(path));   // unchanged
    }

    [Fact]
    public void Replace_all_replaces_every_occurrence()
    {
        var path = Path.Combine(_tmp, "a.txt");
        File.WriteAllText(path, "foo bar foo baz foo");

        var result = EditFile.Run(path, "foo", "FOO", replace_all: true);

        Assert.Contains("replaced 3 occurrences", result);
        Assert.Equal("FOO bar FOO baz FOO", File.ReadAllText(path));
    }

    [Fact]
    public void Returns_error_for_empty_old_string()
    {
        var path = Path.Combine(_tmp, "a.txt");
        File.WriteAllText(path, "anything");

        var result = EditFile.Run(path, "", "x");

        Assert.StartsWith("error:", result);
        Assert.Contains("old_string", result);
    }

    [Fact]
    public void Returns_error_for_empty_path()
    {
        var result = EditFile.Run("", "a", "b");

        Assert.StartsWith("error:", result);
        Assert.Contains("path", result);
    }

    [Fact]
    public void Preserves_surrounding_content()
    {
        var path = Path.Combine(_tmp, "a.txt");
        File.WriteAllText(path, "line1\nfind-me\nline3\n");

        var result = EditFile.Run(path, "find-me", "FOUND");

        Assert.Contains("replaced 1 occurrence", result);
        Assert.Equal("line1\nFOUND\nline3\n", File.ReadAllText(path));
    }
}
