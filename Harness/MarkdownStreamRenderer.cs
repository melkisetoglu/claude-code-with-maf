// =============================================================================
//  MarkdownStreamRenderer — streams text to the console with a single
//  flourish: triple-backtick code blocks render in dim cyan, the rest is
//  the terminal's default colour.
//
//  Workshop scope: NOT a real markdown renderer. There's no language
//  tokenization, no keyword colouring, no header / list / link rendering.
//  Step 9's goal is "code blocks look different from prose," not "render
//  markdown beautifully."
//
//  Streaming detail: text arrives in arbitrary-sized chunks. We track
//  whether we're currently inside a fence (`_inCodeBlock`) and toggle on
//  every "```" we see. Single-chunk fences are handled correctly; fences
//  split across chunks (e.g. "``" then "`csharp\n") are not — the literal
//  triple-backtick won't toggle until both chunks have been written. The
//  display reads slightly wrong for one chunk, then self-corrects.
//  Workshop-acceptable; called out in the chapter pitfalls.
//
//  Colour guard: if stdout is redirected (piped, smoke tests, file capture)
//  we skip the ANSI escapes so captured output stays plain.
// =============================================================================

namespace ClaudeChat.Harness;

public sealed class MarkdownStreamRenderer
{
    // ANSI: "" is ESC. "[2;36m" sets dim + cyan; "[0m" resets all.
    private const string DimCyan = "[2;36m";
    private const string ResetSgr = "[0m";

    private readonly bool _colorsEnabled;
    private bool _inCodeBlock;

    public MarkdownStreamRenderer()
    {
        _colorsEnabled = !Console.IsOutputRedirected;
    }

    /// <summary>True while we're inside a fenced code block.</summary>
    public bool InCodeBlock => _inCodeBlock;

    /// <summary>
    /// Write a streamed text chunk, toggling colour around triple-backtick
    /// fences found in the chunk.
    /// </summary>
    public void Write(string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
        {
            return;
        }

        // Always parse fences and track state — InCodeBlock is part of the
        // contract regardless of whether we're emitting ANSI colour. Only
        // the colour-escape Writes are guarded.
        var parts = chunk.Split("```");
        for (int i = 0; i < parts.Length; i++)
        {
            Console.Write(parts[i]);
            if (i < parts.Length - 1)
            {
                Console.Write("```");
                _inCodeBlock = !_inCodeBlock;
                if (_colorsEnabled)
                {
                    Console.Write(_inCodeBlock ? DimCyan : ResetSgr);
                }
            }
        }
    }

    /// <summary>
    /// Reset the terminal color and clear the in-block flag. Called at the
    /// end of every streaming round-trip so an unbalanced fence (model
    /// stopped mid-block) doesn't leak colour into the prompt.
    /// </summary>
    public void Reset()
    {
        if (_inCodeBlock && _colorsEnabled)
        {
            Console.Write(ResetSgr);
        }
        _inCodeBlock = false;
    }
}
