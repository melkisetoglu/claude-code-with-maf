# Step 01 — First read-only tool: `read_file`

> *Goal: the model can read your files. This is the step where the chat wrapper becomes an agent.*

In Step 0 the model could only talk *about* code; it had to guess at what your files actually contained. That's chat. In Step 1 we register a single function — `read_file` — and the model decides when to call it, what path to pass, and how to weave the result into its reply. **That decision-making loop is what "agent" means.** Everything else in this tutorial — more tools, approval gates, sub-agents, MCP — is variations on this same loop.

## What you'll have at the end

```
$ dotnet run
Started new session: 7f3c91a8
Model: claude-haiku-4-5. Commands: '/exit' (quit), '/clear' (new session), '/id' (show id).

you > what's in README.md?
claude >
[read_file: README.md]
The README describes a workshop that grows a Claude Code-style console agent on
top of Microsoft Agent Framework in .NET. It covers streaming chat, persistent
named sessions, and how to resume them — Step 0 baseline.

you > does TUTORIAL.md mention compaction?
claude >
[read_file: TUTORIAL.md]
Yes — Step 10 in Milestone 4 is "Context compaction (CompactionProvider)".
```

The `[read_file: README.md]` line on its own row is the visible agentic loop: the model decided it needed the file, called the tool, the tool ran, the result came back, the model continued. Without that line you'd see the answer but not the reasoning trail.

## MAF concepts introduced

| Concept | What it is |
|---|---|
| `AITool` / `AIFunction` | The base abstraction for "thing the model can call." From `Microsoft.Extensions.AI.Abstractions`. |
| `AIFunctionFactory.Create` | Wraps a plain .NET method (or delegate) as an `AIFunction`. Generates the JSON schema from the method signature + `[Description]` attributes. |
| `[Description]` attribute | What the model sees. The method's description becomes the tool description; each parameter's `[Description]` becomes a parameter schema entry. **The model only ever sees these strings — write them like docs for the model.** |
| `tools:` on `AsAIAgent(...)` | Pass an `IList<AITool>`. That's the whole registration step. |
| `FunctionInvokingChatClient` | M.E.AI's auto-invoker, wired in under the hood. It handles the call → run → result loop. We don't write it. |
| `update.Contents` (`IList<AIContent>`) | Each streamed update has a list of content items. Step 0 we only used `update.Text`; Step 1 we look for `FunctionCallContent` too. |
| `FunctionCallContent` | The "the model wants to call this function" content item. Carries `Name`, `Arguments`, `CallId`. |
| `FunctionResultContent` | The result of a tool invocation. Mostly internal — by the time we see streaming text again, the result has already been folded into the model's response. |

## Setup

No new dependencies; everything we need is already in `Microsoft.Extensions.AI` (transitive via `Microsoft.Agents.AI.Anthropic`).

One new folder:

```
Tools/ReadFile.cs
```

(Per [CLAUDE.md](../CLAUDE.md), `Tools/` is the home for all function tools — Steps 2–4 add `list_dir`, `glob`, `grep`, `write_file`, `edit_file`, `bash` here.)

## Walkthrough

### The tool itself — `read_file`

*In [`Tools/ReadFile.cs`](../Tools/ReadFile.cs).*

```csharp
public static class ReadFile
{
    private const int MaxBytes = 100_000;

    [Description("Read the contents of a text file at the given path. " +
                 "Returns the file's text. " +
                 "If the file is missing, too large (>100KB), or not readable as text, " +
                 "returns a short error message instead of throwing.")]
    public static string Read(
        [Description("Absolute or relative file path. Relative paths resolve against the current working directory.")]
        string path)
    {
        try
        {
            if (!File.Exists(path))
                return $"error: no file at '{path}'";
            var info = new FileInfo(path);
            if (info.Length > MaxBytes)
                return $"error: file '{path}' is {info.Length} bytes, exceeds the {MaxBytes}-byte limit for read_file";
            return File.ReadAllText(path);
        }
        catch (UnauthorizedAccessException) { return $"error: permission denied reading '{path}'"; }
        catch (Exception ex)                { return $"error: {ex.GetType().Name}: {ex.Message}"; }
    }
}
```

Three things to notice:

1. **It's a plain method.** No special base class, no MAF type. The framework uses reflection on the method signature + attributes to generate the schema the model sees.
2. **Errors return strings, not exceptions.** When the read fails, the model gets `error: no file at '...'` as the function result, reads it, and recovers — usually by trying a different path or telling the user. Throwing crashes the turn.
3. **Descriptions are model-facing docs.** This is the only thing the model sees about the tool. Vague descriptions → tool gets called wrongly or skipped. Be specific about what it does, what it returns, and how errors look.

> **No path scoping yet.** This tool will happily read `/etc/passwd` if asked. That's fine for Step 1 — the security gate is Step 3 (`ToolApprovalAgent`). Mixing concerns now would muddy what each step teaches.

### Registering the tool

*In [`Agent/AgentBuilder.cs`](../Agent/AgentBuilder.cs).*

```csharp
var tools = new List<AITool>
{
    AIFunctionFactory.Create(ReadFile.Read, name: "read_file"),
};

return client.AsAIAgent(
    model: model,
    name: "ClaudeChat",
    instructions: "You are a helpful assistant. Keep replies concise. " +
                  "When you need the contents of a file, call the read_file tool.",
    tools: tools);
```

Two small details:

- We override `name:` to be `read_file` (snake_case). Without it, the tool would be named `Read` after the method. Snake_case is the convention models are trained on.
- The instructions get a one-liner about the tool. Not strictly required — the model will call any registered tool when relevant — but a nudge tightens behavior, especially on smaller models.

`AsAIAgent` quietly wraps the underlying chat client with `FunctionInvokingChatClient` when `tools:` is non-empty. That's the auto-invoker: when the model emits a tool-call, M.E.AI runs the function, captures the return value, and feeds it back as a `FunctionResultContent` — all before we see the next streamed update. **We never wrote the tool-execution loop.** That's the M.E.AI / MAF stack earning its keep.

### Surfacing tool calls in the stream

*In [`Harness/ChatLoop.cs`](../Harness/ChatLoop.cs).*

In Step 0 we wrote `update.Text` and called it a day. That hides the agentic loop. Step 1 iterates `update.Contents`:

```csharp
await foreach (var update in agent.RunStreamingAsync(input, session))
{
    foreach (var content in update.Contents)
    {
        switch (content)
        {
            case TextContent text:
                Console.Write(text.Text);
                break;
            case FunctionCallContent call:
                var firstArg = call.Arguments?.Values.FirstOrDefault();
                Console.WriteLine($"\n[{call.Name}: {firstArg}]");
                break;
        }
    }
}
```

Why iterate `Contents` instead of using `update.Text`? Because `update.Text` is just a flattened convenience — for tool calls it's empty, and you'd never see them. The full picture is in `Contents`.

We deliberately *don't* render `FunctionResultContent` (the tool's return value). It would be either redundant (the model summarizes it in its next text chunk) or huge (an entire file dumped to the terminal). Step 4, when mutations land, will show results because *that's* when seeing what the tool actually did matters.

## Verify

```bash
dotnet build
dotnet run -- --help    # smoke check: no-API paths still work
```

Then a real turn:

```bash
dotnet run
you > what's in README.md?
```

You should see a `[read_file: README.md]` line, then the model's summary. Try a missing file:

```
you > what's in nonexistent.txt?
```

The model should call `read_file`, see the `error: no file at 'nonexistent.txt'` return, and tell you the file doesn't exist — without crashing.

Try a file outside cwd:

```
you > read /etc/hosts and tell me what's there
```

It should work (no path scoping at this step). If you see "permission denied" that's the OS, not us.

Try size limit:

```bash
dd if=/dev/urandom bs=1024 count=200 of=/tmp/big.bin 2>/dev/null
# in chat:
you > read /tmp/big.bin
```

The tool returns the size-limit error; the model relays it.

## Pitfalls

### Bad `[Description]` text → bad tool use

The model only sees the strings. If your description says "reads files" and nothing else, it'll guess — sometimes pass the wrong arg, sometimes skip the tool entirely. Say what it returns, what failure looks like, what the parameter expects. Treat the descriptions as the tool's API docs for an audience that has read no other code.

### Don't throw — return error strings

If `read_file` throws on a missing path, the *whole turn* fails. The model can't see the exception. Returning `"error: no file at '...'"` lets the model adjust: "I see, that file doesn't exist; let me try another path." This is convention across well-built agent toolkits, not a quirk of MAF.

### `update.Text` hides tool calls

A common Step-0-to-Step-1 mistake: keep printing `update.Text` and wonder why tool calls don't appear. They never do — `Text` is a flattened convenience. You have to iterate `Contents` to see anything beyond plain text.

### The model may read the same file twice in one turn

If the user's question is ambiguous (`"what's in this project?"`), the model will sometimes call `read_file` two or three times in a single response. That's fine — but the cost adds up. Step 5 (logging + cost tracking) makes this visible; Step 17 (budgets) lets you cap it.

### Argument display is naive

We show `[read_file: <first arg value>]` — fine for one-arg tools. Step 2's `glob`/`grep` have multiple args; that's the moment to render the full `Arguments` dict, not retrofit it now.

## Stretch exercises

- **`max_bytes` parameter.** Let the model pass a smaller cap when it knows it only needs a snippet. Be careful — the model usually overestimates how much it needs.
- **Line-range read.** `read_file(path, start_line, end_line)`. This is what Claude Code does. Worth comparing to "always whole file" once you have both.
- **Encoding detection.** UTF-8 is the default of `File.ReadAllText`; try a Latin-1 file and see what the model does with the resulting mojibake.
- **Tool-call counter in the banner.** Track how many times the tool was called this session and print it in the welcome line on resume. Foundation for the `/cost` command in Step 7.
- **Print tool durations.** Wrap the function call to time it; surface `[read_file: README.md (3ms)]`. Useful sanity check that disk I/O isn't a bottleneck.

## Where the seams are

What this step deliberately doesn't have:

- **No path scoping.** Step 3's approval gate is the right place.
- **No mutation tools.** Step 4 adds `write_file`/`edit_file`/`bash`, all gated.
- **No richer tool-result rendering.** Step 4 + Step 9 (streaming polish) handle this together.
- **No cost/duration display.** Step 5.
- **No multi-arg tool-call display.** Step 2 introduces `list_dir`, `glob`, `grep` — that's the moment to upgrade.

## Next

→ **Step 02 — file-system toolset: `list_dir`, `glob`, `grep`** *(planned)*

By the end of Step 2 the model can navigate a codebase blindfolded — find files by pattern, search by content, list directories — without you having to paste paths. That's when an agent stops feeling like a chatbot.
