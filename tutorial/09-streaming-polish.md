# Step 09 — Streaming polish: Ctrl+C interrupt, spinner, code-block colour

> *Goal: the REPL stops feeling like a basic line dump and starts behaving like a real interactive tool. Long answers can be cancelled. The terminal shows the model is working before the first token arrives. Code blocks visually distinct from prose.*

Three small UX features land in this step, none of them MAF-related. **The interesting bit is the discipline of doing UX without breaking automation** — the same binary needs to feel polished when running in a terminal *and* produce clean ASCII when piped to a smoke-test or log file.

## What you'll have at the end

When you run interactively:

```
you > write me a hello-world c# program
claude > ⠋     ← spinner spins until the first content arrives
claude > Here you go:
```csharp                   ← from here, dim cyan
public class Program
{
    public static void Main() => Console.WriteLine("Hello, World!");
}
```                          ← back to default colour
(turn: 2050 in + 47 out, $0.0023)
```

Press Ctrl+C mid-reply:

```
you > write a 500-word essay about monoids
claude > A monoid is an algebraic structure consisting of a set together with
an associative binary operation^C
(interrupted)
(turn: 2042 in + 38 out, $0.0023)
```

The process stays alive, the session is saved, you're back at the prompt.

## MAF concepts introduced

**None.** Step 9 is pure harness UX. The three features each pick up a standard .NET pattern:

| Feature | Standard pattern |
|---|---|
| Ctrl+C interrupt | `Console.CancelKeyPress` + per-turn `CancellationTokenSource` + `IAsyncEnumerable.WithCancellation(token)` |
| Spinner | Background `Task` writing/erasing a glyph, awaited via `IAsyncDisposable.StopAsync()` |
| Code-block colour | ANSI escape sequences (`(char)27 + "[2;36m"`, etc.) guarded by `Console.IsOutputRedirected` |

## Setup

No new packages. Two new files, one big ChatLoop edit:

```
Harness/Spinner.cs                — IAsyncDisposable spinner with a background task
Harness/MarkdownStreamRenderer.cs — fence-aware writer that toggles colour around ```
```

## Walkthrough

### The spinner

*In [`Harness/Spinner.cs`](../Harness/Spinner.cs).*

The spinner runs as a background `Task`:

```csharp
public Spinner()
{
    _enabled = !Console.IsOutputRedirected;
    _loop = _enabled ? Task.Run(RunAsync) : Task.CompletedTask;
}

private async Task RunAsync()
{
    int i = 0;
    bool drawn = false;
    try
    {
        while (!_stop.IsCancellationRequested)
        {
            Console.Write(Frames[i % Frames.Length]);
            drawn = true;
            try { await Task.Delay(FrameMs, _stop.Token); }
            catch (OperationCanceledException) { /* fall through to erase */ }
            Console.Write("\b \b");   // erase glyph + reset cursor
            drawn = false;
            i++;
        }
    }
    finally
    {
        if (drawn) try { Console.Write("\b \b"); } catch { }
    }
}
```

Three deliberate choices:

1. **`Console.IsOutputRedirected` guard.** If output is being piped (smoke tests, file capture, `dotnet run | tee log`), the spinner is a no-op. Without this, captured output would be sprinkled with Braille glyphs and `\b` (backspace) bytes — making `grep` against captured logs miserable.
2. **`Task.Delay(FrameMs, _stop.Token)`.** When `StopAsync()` cancels the token, the delay aborts immediately. Without the token, stopping the spinner would have to wait up to 80ms for the next frame. Snappy is better.
3. **`\b \b` erase pattern.** Backspace, overwrite with space, backspace again. Standard CLI trick. Leaves the cursor exactly where the glyph was, ready for the next write.

The `StopAsync()` method awaits the loop task before returning:

```csharp
public async ValueTask StopAsync()
{
    if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
    if (!_enabled) return;

    _stop.Cancel();
    try { await _loop.ConfigureAwait(false); } catch { }
}
```

**That await is load-bearing.** It guarantees the spinner has finished its erase-and-exit dance before `StopAsync` returns. Then the main task can safely write content — no race between the spinner re-drawing a glyph and the streamed text. The `Interlocked.Exchange` makes the method idempotent so `StopAsync` is safe to call multiple times.

### The Markdown stream renderer

*In [`Harness/MarkdownStreamRenderer.cs`](../Harness/MarkdownStreamRenderer.cs).*

A real markdown renderer tokenizes by language, applies syntax-aware colouring per keyword, handles headings, lists, links, inline code, etc. **We're shipping one flourish only**: triple-backtick fenced code blocks render in dim cyan; everything else is the terminal default.

```csharp
public void Write(string chunk)
{
    if (string.IsNullOrEmpty(chunk)) return;

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
                Console.Write(_inCodeBlock ? DimCyan : ResetSgr);
        }
    }
}
```

A few subtle points:

- **State is always tracked, colour is conditionally emitted.** Tests assert `InCodeBlock`; consumers (theoretical or actual) might rely on it. The colour-emission is the *side effect*, the state is the *contract*.
- **`chunk.Split("```")` is the entire fence-detection algorithm.** No regex, no streaming parser. The cost: a fence split across two chunks (`"intro``"` then `` "`code\n```\n" ``) is misclassified — see the pitfall below.
- **ANSI constants built via `(char)27 + "[2;36m"`** rather than embedded ESC literals. Some editors and tooling silently strip ESC characters; building at field-init time sidesteps that.

`Reset()` is called at the end of every streaming round-trip:

```csharp
public void Reset()
{
    if (_inCodeBlock && _colorsEnabled)
        Console.Write(ResetSgr);
    _inCodeBlock = false;
}
```

This handles the case of an *unbalanced* fence — the model wrote `` ``` `` to open a block and then stopped (network blip, finish reason, etc.) without writing the closing `` ``` ``. Without `Reset`, the terminal would stay in dim cyan and the next `you > ` prompt would render dimly. The `Reset` is the "always restore the terminal to a known state at function boundaries" hygiene rule.

### Cancellation wiring in ChatLoop

*In [`Harness/ChatLoop.cs`](../Harness/ChatLoop.cs).*

```csharp
using var turnCts = new CancellationTokenSource();
void OnCancel(object? sender, ConsoleCancelEventArgs e)
{
    e.Cancel = true;        // keep the process alive
    turnCts.Cancel();       // cancel just this turn
}
Console.CancelKeyPress += OnCancel;

try
{
    while (true) { /* ... streaming with .WithCancellation(turnCts.Token) ... */ }
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n(interrupted)");
}
finally
{
    Console.CancelKeyPress -= OnCancel;
}
```

Three deliberate choices here too:

1. **`e.Cancel = true` is what keeps the process alive.** The default behaviour of Ctrl+C is to call `Environment.Exit`. Setting `e.Cancel = true` says *"I'm handling this; don't terminate the process."* Without this line, Ctrl+C exits — usually not what an interactive tool wants.
2. **`turnCts` is per-turn.** A new `CancellationTokenSource` is constructed each chat turn and disposed when the turn ends (`using var`). The next turn gets a fresh source. Avoids the bug class of *"Ctrl+C cancellation leaked from a previous turn."*
3. **Hook is added and removed inside `try`/`finally`.** Forgetting the `-=` would slowly leak handlers across turns — every old turn's handler would also fire on a new Ctrl+C, each trying to cancel a CTS that has already been disposed. The `finally` block keeps the handler set clean.

Inside the streaming loop, the cancellation token threads through:

```csharp
await foreach (var update in agent.RunStreamingAsync(nextMessage, ctx.Session)
    .WithCancellation(turnCts.Token))
{
    // ...
}
```

`WithCancellation` is the standard `IAsyncEnumerable<T>` extension. When the token fires, the next `MoveNextAsync` call throws `OperationCanceledException`, which propagates out of the foreach and into our `catch` block. **The model isn't notified** — the HTTP request stays open from Anthropic's end, but we stop consuming the stream. The framework cleans up.

The session state up to the cancellation point is preserved (MAF retains whatever was already captured), so the next `await SessionStore.SaveAsync(...)` writes whatever the model managed to produce before the interrupt. **Resuming an interrupted session works.**

### Tying it all together: per-turn lifecycle

The chat-turn block in ChatLoop now looks like this (simplified):

```csharp
var renderer = new MarkdownStreamRenderer();
using var turnCts = new CancellationTokenSource();
Console.CancelKeyPress += OnCancel;

try
{
    while (true)  // approval round-trip loop
    {
        var spinner = new Spinner();
        bool spinnerStopped = false;

        async ValueTask StopSpinnerOnce()
        {
            if (spinnerStopped) return;
            spinnerStopped = true;
            await spinner.StopAsync();
        }

        try
        {
            await foreach (var update in agent.RunStreamingAsync(...).WithCancellation(turnCts.Token))
            {
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case TextContent text when text.Text.Length > 0:
                            await StopSpinnerOnce();
                            renderer.Write(text.Text);
                            break;
                        case FunctionCallContent call:
                            await StopSpinnerOnce();
                            Console.WriteLine($"\n[{FormatCall(call)}]");
                            break;
                        // ... approval / usage handling ...
                    }
                }
            }
        }
        finally
        {
            await StopSpinnerOnce();   // ensure spinner is dead before next iteration
            renderer.Reset();
        }

        if (pendingApprovals.Count == 0) break;
        // ... approval responses ...
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n(interrupted)");
}
finally
{
    Console.CancelKeyPress -= OnCancel;
}
```

Each approval round-trip gets a fresh spinner. The renderer is shared across the whole turn so its `_inCodeBlock` state survives approval prompts (a code block opened before the model called a tool can still close after the tool ran). The `finally` blocks at every level ensure the spinner is stopped and the terminal colour is reset, no matter how control leaves.

## Verify

```bash
dotnet build
dotnet test                 # 133 unit tests (123 prior + 10 new)
```

**Piped run** (smoke-test mode — colors and spinner auto-off):

```bash
echo "show a c# hello-world in a code block" | dotnet run --no-build | od -c | grep -c '033'
# Expect: 0
# (No ANSI escapes in piped output. Color guard verified.)
```

**Interactive run** (the visible-polish bits):

```bash
dotnet run
# Type a question that produces a code block:
you > write a c# hello-world program
# Watch for:
#   1. A spinning Braille glyph appears next to "claude > " before the first token.
#   2. The glyph disappears once text starts streaming.
#   3. Anything between ``` fences renders in dim cyan; the rest is default.

# Type a long question to test Ctrl+C:
you > write a 500-word essay about monoids
# Press Ctrl+C while text is streaming.
# Expect: "(interrupted)" on its own line, per-turn token line still prints,
# you return to the prompt, the process stays alive.
```

## Pitfalls

### Fences split across chunks misclassify state

`chunk.Split("```")` only sees fences contained within a single chunk. If chunk 1 ends with two backticks and chunk 2 begins with one, the renderer doesn't detect the fence — the toggle is missed. The display reads slightly wrong (a code block that should have started looks like prose, or vice versa) until the next fence comes through.

The fix is a proper streaming parser with a small lookahead buffer; ~30 LOC. Workshop-acceptable to skip — the failure mode is cosmetic and self-corrects.

### Cancelling consumes tokens — and the displayed count under-counts

`UsageContent` arrives in the **final** streaming update of each `RunStreamingAsync` call — it's the server's "here's your bill" capstone. Interrupting mid-stream means we never receive it for the in-flight sub-call, but the server has already billed:

- the full input prompt (system prompt + history + tool schemas + user message — easily ~2000 tokens *at minimum* in this workshop's setup);
- whatever output it generated up to the moment our client stopped reading.

So our per-turn line reads `(turn: 0 in + 0 out, $0.0000)` on an interrupted single-call turn even though tokens were really consumed. **The display is honest about its uncertainty:** when interrupted, the line changes to either `(turn (partial): N in + M out, $X)` followed by `(tokens beyond this partial count were still billed by the server)`, or — if we got nothing — a one-line note explaining usage is unavailable and the server still billed.

If you want the real number, Anthropic's billing dashboard is the source of truth. Step 17 (budgets) revisits per-turn accuracy.

### Ctrl+C at the prompt still exits the process

We only handle `Console.CancelKeyPress` while a turn is in flight. At the `you > ` prompt, Ctrl+C calls the default handler (`Environment.Exit`). Some users will expect double-Ctrl+C to exit and single-Ctrl+C to clear the current line — that's Claude Code's pattern but it requires hijacking the readline at the prompt as well. Out of scope for Step 9.

### Spinner write race

`Console.Write` is not threadsafe across multiple writers. The spinner runs on a thread pool thread; the main task writes content. The race is *closed by the await* in `StopAsync` — we don't write content until the spinner task has exited. If you ever skip that await (e.g. forget the `await` keyword on `StopSpinnerOnce()`), you'll see interleaved glyphs and content in the wild. Watch for it in review.

### Colors leak into prompts on unbalanced fences

If the model writes `` ``` `` to open a block and then stops without writing the closing fence (network blip, finish-reason without closing), the terminal stays in dim cyan. The next `you > ` prompt would render dimly. `renderer.Reset()` at the end of every round-trip fixes this — *do not skip it* in any refactor.

### ESC character is silently stripped by some tools

The ANSI escape character (`(char)27`, ASCII 0x1B) gets eaten by some editors, some terminal multiplexers, and some patching tools. Building the constants at field-init time via `(char)27 + "[2;36m"` keeps the source text plain ASCII and the runtime value correct. **Don't paste literal ESC bytes into source files.** This came up live during Step 9's development.

### Spinner glyph alignment

Braille characters are double-wide in some terminal fonts. The `\b \b` erase pattern assumes one column of cursor movement; on a font that renders Braille as two columns, the second column won't be erased and a faint residual character can linger. Workshop-acceptable; if it bothers you, swap the frames for ASCII (`/-\|`).

### The `Reset` method is conditional, not idempotent

`Reset` only writes `ResetSgr` if `_inCodeBlock` was true. After Reset, calling Reset again is a no-op (the flag is already false). Tests confirm — good — but if you ever change the implementation to *always* emit Reset, you'll see one extra escape per call. Keep the conditional.

## Stretch exercises

- **Double-Ctrl+C to exit.** First press cancels the turn; second press within ~2 seconds exits the process. Standard CLI pattern.
- **Per-language code-block colouring.** Detect `language` after the opening fence and pick a colour per language (Csharp = dim cyan, JS = dim yellow, ...). Real syntax highlighting needs `Spectre.Console` or `markdig` plus a colour renderer.
- **Spinner messages.** Beyond a glyph: `"⠋ thinking…"` for the first second, `"⠋ planning…"` after 5s of no output, etc. Hint-driven UX.
- **Token-rate indicator.** Replace the spinner with `"⠋ 42 tok/s"` once tokens start streaming. Reads the per-update token count.
- **Save-on-interrupt vs discard-on-interrupt as a config knob.** Some users want "if I Ctrl+C, throw away the partial turn entirely; don't save it." A `saveOnInterrupt: false` flag in `agent.json`.
- **Status line at the prompt.** A persistent right-aligned status (model name, session id) using ANSI cursor positioning.

## Where the seams are

What this step deliberately doesn't have:

- **Real syntax highlighting.** Single fence-aware colour, not per-language tokenization.
- **Double-Ctrl+C UX.** Single Ctrl+C interrupts; default Ctrl+C at the prompt still exits.
- **Streaming parser with lookahead.** Per-chunk split is good enough.
- **Token-rate display.** Per-turn summary line is the only metric shown.
- **Status line / banners.** Just the prompt label changes.

## Next

→ **Step 10 — Context compaction (`CompactionProvider`)** *(planned)*

Up next: when the session history gets long enough to threaten the model's context window, MAF can compact older turns into a summary while preserving recent detail. That's the last step in Milestone 4 — after which the harness UX is essentially complete.
