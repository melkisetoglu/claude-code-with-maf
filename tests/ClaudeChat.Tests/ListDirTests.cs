// =============================================================================
//  ListDirTests — covers list_dir tool: happy path, dotfile skip, missing
//  dir, dir-first ordering, empty-dir message.
// =============================================================================

using ClaudeChat.Tools;

namespace ClaudeChat.Tests;

[Trait("Category", "Unit")]
public sealed class ListDirTests : IDisposable
{
    private readonly string _tmp;

    public ListDirTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "claudechat-listdir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Lists_files_with_sizes()
    {
        File.WriteAllText(Path.Combine(_tmp, "a.txt"), "hi");
        File.WriteAllText(Path.Combine(_tmp, "b.md"), "hello");

        var output = ListDir.Run(_tmp);

        Assert.Contains("a.txt  2 bytes", output);
        Assert.Contains("b.md  5 bytes", output);
    }

    [Fact]
    public void Lists_subdirectories_first()
    {
        Directory.CreateDirectory(Path.Combine(_tmp, "sub"));
        File.WriteAllText(Path.Combine(_tmp, "z_after.txt"), "");

        var output = ListDir.Run(_tmp);

        var subIdx = output.IndexOf("sub/", StringComparison.Ordinal);
        var fileIdx = output.IndexOf("z_after.txt", StringComparison.Ordinal);
        Assert.True(subIdx >= 0 && fileIdx >= 0);
        Assert.True(subIdx < fileIdx, "subdirectories should be listed before files");
        Assert.Contains("sub/  (dir)", output);
    }

    [Fact]
    public void Skips_dotfiles_and_dotdirs()
    {
        File.WriteAllText(Path.Combine(_tmp, ".hidden"), "");
        Directory.CreateDirectory(Path.Combine(_tmp, ".git"));
        File.WriteAllText(Path.Combine(_tmp, "visible.txt"), "");

        var output = ListDir.Run(_tmp);

        Assert.DoesNotContain(".hidden", output);
        Assert.DoesNotContain(".git", output);
        Assert.Contains("visible.txt", output);
    }

    [Fact]
    public void Returns_error_for_missing_directory()
    {
        var missing = Path.Combine(_tmp, "nope");

        var output = ListDir.Run(missing);

        Assert.StartsWith("error:", output);
        Assert.Contains(missing, output);
    }

    [Fact]
    public void Reports_empty_directory()
    {
        var output = ListDir.Run(_tmp);

        Assert.Contains("empty directory", output);
    }
}
