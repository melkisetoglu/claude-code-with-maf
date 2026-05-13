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

    // -------- Caps that prevent runaway output ---------------------------
    // Regression coverage for the sessions-folder incident: a single grep
    // across sessions/*.json hit JSON-encoded tool results with single
    // lines >30 KB long, returning ~250 KB of matches that then blew the
    // model's context window on the next turn.

    [Fact]
    public void Truncates_very_long_matched_line()
    {
        // Build a 5,000-char line containing the match. Pre-fix this would
        // be returned verbatim; post-fix it's truncated to ~500 chars with
        // an explicit "<line truncated, N chars total>" marker.
        var path = Path.Combine(_tmp, "big.json");
        var bigLine = "prefix " + new string('x', 4900) + " NEEDLE suffix";
        File.WriteAllText(path, bigLine + "\n");

        var output = Grep.Run("NEEDLE", path);

        // The full long line must NOT appear in the output.
        Assert.DoesNotContain(bigLine, output);
        // The match marker (file:line:) is still there.
        Assert.Contains($"{path}:1:", output);
        // And we tell the caller the line was longer than what's shown.
        Assert.Contains("line truncated", output);
        Assert.Contains($"{bigLine.Length} chars total", output);
        // Soft size check — the entire output for a single match shouldn't
        // be remotely close to the original line's size.
        Assert.True(output.Length < 1_000,
            $"single-match output should be <1KB after truncation, was {output.Length}");
    }

    [Fact]
    public void Caps_total_output_size_when_many_long_matches()
    {
        // 200 files each with a 2 KB matching line. Uncapped, that's ~400 KB
        // of output. Even with the per-line cap (500 chars) that's still
        // 200 × ~520 = ~104 KB of output, which still busts the 50 KB cap.
        // Post-fix: output stops once it crosses MaxOutputChars and announces it.
        for (int i = 0; i < 200; i++)
        {
            var p = Path.Combine(_tmp, $"f{i:000}.json");
            File.WriteAllText(p, "lead " + new string('y', 1900) + " NEEDLE tail\n");
        }

        var output = Grep.Run("NEEDLE", _tmp);

        Assert.Contains("output exceeded", output);
        // The cap is 50 KB. The "truncated" marker is appended after we
        // detect the cross, so a small overshoot is expected — give some
        // headroom rather than asserting strictly < 50_000.
        Assert.True(output.Length < 55_000,
            $"output should stop near the 50 KB cap, was {output.Length}");
    }
}
