// =============================================================================
//  MarkdownStreamRendererTests — covers the fence-toggle state machine.
//
//  These tests run with stdout redirected (the test runner pipes it), so
//  the renderer's color guard auto-disables ANSI escapes. We assert on the
//  state flag (InCodeBlock) directly, plus the plain-text output that lands
//  in the redirected stdout.
// =============================================================================

using ClaudeChat.Harness;

namespace ClaudeChat.Tests;

[Trait("Category", "Unit")]
[Collection("Console-shared-static")]
public sealed class MarkdownStreamRendererTests : IDisposable
{
    private readonly TextWriter _previousOut;
    private readonly StringWriter _capturedOut = new();

    public MarkdownStreamRendererTests()
    {
        _previousOut = Console.Out;
        Console.SetOut(_capturedOut);
    }

    public void Dispose()
    {
        Console.SetOut(_previousOut);
    }

    [Fact]
    public void Plain_text_passes_through_unchanged()
    {
        var r = new MarkdownStreamRenderer();

        r.Write("hello, world");
        r.Reset();

        Assert.Equal("hello, world", _capturedOut.ToString());
        Assert.False(r.InCodeBlock);
    }

    [Fact]
    public void Single_chunk_with_balanced_fence_toggles_in_and_out()
    {
        var r = new MarkdownStreamRenderer();

        r.Write("here:\n```csharp\nint x = 1;\n```\ndone");

        Assert.False(r.InCodeBlock);   // ended outside block
        var output = _capturedOut.ToString();
        Assert.Contains("here:", output);
        Assert.Contains("int x = 1;", output);
        Assert.Contains("done", output);
    }

    [Fact]
    public void Open_fence_without_close_leaves_renderer_in_code_block()
    {
        var r = new MarkdownStreamRenderer();

        r.Write("intro:\n```csharp\nint x = 1;\n");

        Assert.True(r.InCodeBlock);
    }

    [Fact]
    public void Reset_clears_in_code_block_flag()
    {
        var r = new MarkdownStreamRenderer();
        r.Write("```");
        Assert.True(r.InCodeBlock);

        r.Reset();

        Assert.False(r.InCodeBlock);
    }

    [Fact]
    public void Fence_split_across_two_chunks_misclassifies_state()
    {
        // Acknowledged limitation: per-chunk Split("```") can't see fences
        // that straddle chunks. In this test the *intended* markdown is
        //   "intro``" + "`code\n```\n"
        // which reads (after concatenation) as "intro```code\n```\n" — an
        // opened and then closed fence around "code\n". A real markdown
        // parser would end up outside the block.
        //
        // Our renderer sees:
        //   chunk 1: "intro``"           — no fence visible in this chunk
        //   chunk 2: "`code\n```\n"      — exactly one fence visible → toggle on
        // So it ends INSIDE a block instead of outside. This test pins the
        // wrong behaviour as a regression check; the chapter's pitfalls
        // section calls it out as expected.
        var r = new MarkdownStreamRenderer();

        r.Write("intro``");
        Assert.False(r.InCodeBlock);

        r.Write("`code\n```\n");
        Assert.True(r.InCodeBlock);   // wrong, but documented
    }

    [Fact]
    public void Empty_chunk_is_a_noop()
    {
        var r = new MarkdownStreamRenderer();

        r.Write("");

        Assert.Equal("", _capturedOut.ToString());
        Assert.False(r.InCodeBlock);
    }

    [Fact]
    public void Multiple_chunks_inside_block_stay_in_block()
    {
        var r = new MarkdownStreamRenderer();
        r.Write("```\nline1\n");
        r.Write("line2\n");
        r.Write("line3\n");

        Assert.True(r.InCodeBlock);

        r.Write("```\n");
        Assert.False(r.InCodeBlock);
    }
}
