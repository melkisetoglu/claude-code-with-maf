# Step 07 — Slash commands (`/help`, `/clear`, `/tools`, `/cost`, `/model`, `/sessions`, `/yolo`)

> *Goal: the chat REPL gains a real command dispatcher. The user can introspect (what tools are loaded? what model? what's this cost so far?), control (clear, yolo-bypass), and navigate (list past sessions) without leaving chat.*

Steps 0–6 used an inline `if (input == "/exit") break;` chain for three commands. Step 7 adds *seven* more, so we replace the chain with a small dispatcher: a name → `ISlashCommand` registry plus a mutable context the commands share. It's also where two promises from earlier chapters finally land — the `/yolo` bypass and "always approve this tool" memory from Step 3's wish-list.

This step is **harness UX, not MAF**. The AIAgent chain doesn't change. The commands live in `Harness/Commands/SlashDispatch.cs`; ChatLoop's main loop shrinks because all the command logic moved out of it.

## What you'll have at the end

```
you > /help

Slash commands:
  /clear       Start a new session in-place (previous stays on disk)
  /cost        Show total token use and cost for this session
  /exit        Exit chat (session is saved)
  /help        Show this help
  /id          Show the current session id
  /model       Show the current model
  /quit        Alias for /exit
  /sessions    List past sessions (newest first)
  /tools       List registered tools and which require approval
  /yolo        Toggle auto-approve-everything mode (off by default)

you > /tools

Tools:
  bash  [approval]
  edit_file  [approval]
  glob
  grep
  list_dir
  read_file
  write_file  [approval]

you > use bash to print hello
claude > 
  approve bash(command="echo hello")? [y/N/a=always]: a
  → approved (and remembering 'bash' for the rest of this session)
[bash: command="echo hello"]
hello.

you > use bash to print world
claude > 
  approve bash(command="echo world")? [auto-approved (always)]
[bash: command="echo world"]
world.

you > /cost
(session: 6331 in + 127 out, $0.0070)

you > /yolo
(yolo: ON — every approval-required tool will auto-approve. /yolo to toggle off.)

you > /exit
```

Notice three things in that transcript:

1. **`a` at the approval prompt** approved AND added `bash` to the always-approve set. The second `bash` call shows no prompt, just `[auto-approved (always)]`.
2. **`/cost`** is the running session total — not just the last turn.
3. **`/yolo` is a toggle**, not a one-shot. State carries across the rest of the session.

## MAF concepts introduced

**None.** Like Step 6, Step 7 is harness ergonomics — pure .NET pattern work. What it teaches is more conventional than MAF-specific:

| Pattern | What it does in Step 7 |
|---|---|
| **Command registry** | `Dictionary<string, ISlashCommand>`. Adding a new command = one entry plus a class. /help reads from the registry itself, so it stays current automatically. |
| **Shared context object** | `SlashContext` holds session state (mutable, so /clear can rotate it) + read-only references (agent, model, accumulators, approval state). Commands depend on the context, not on each other. |
| **Lazy closure for cyclic dependency** | /help needs to know every command including itself. Solved with `new HelpCommand(() => self!.All)` and a `SlashRegistry? self = null` captured in the factory. |
| **Stateful gate ergonomics** | Approval state (yolo flag + always-approve set) lives in an `ApprovalState` object that both the slash commands and `ApprovalPrompt` read. No singleton; testable by passing in a fresh instance. |

## Setup

No new packages. Two new files, one rename:

```
Harness/Commands/SlashDispatch.cs    — ISlashCommand + SlashContext + SlashRegistry + 10 command classes
Harness/Commands/ApprovalState.cs    — YoloMode + AlwaysApprove memory shared by /yolo and ApprovalPrompt
Observability/TurnUsage.cs → UsageAccumulator.cs   — renamed; used for both per-turn AND per-session totals now
```

## Walkthrough

### The dispatcher

*In [`Harness/Commands/SlashDispatch.cs`](../Harness/Commands/SlashDispatch.cs).*

The interface is deliberately tiny:

```csharp
public enum SlashAction { Continue, Exit }

public interface ISlashCommand
{
    string Name { get; }
    string Description { get; }
    SlashAction Run(SlashContext ctx);
}
```

The dispatcher is a name → command map with a simple `TryDispatch`:

```csharp
public SlashAction? TryDispatch(string input, SlashContext ctx)
{
    if (string.IsNullOrEmpty(input) || input[0] != '/') return null;

    var firstSpace = input.IndexOf(' ');
    var name = firstSpace < 0 ? input : input[..firstSpace];

    if (_commands.TryGetValue(name, out var cmd))
        return cmd.Run(ctx);

    Console.WriteLine($"unknown command: {name}. try /help\n");
    return SlashAction.Continue;
}
```

Three readability calls in that small block:

- **Returns `SlashAction?` (nullable).** `null` means "this wasn't a slash command, treat as chat." It's tri-state — Continue, Exit, NotMine. The `null` keeps non-slash input from being misclassified.
- **`TrimEnd` happens before dispatch** (in ChatLoop, the caller). Whitespace from a piped stdin shouldn't change the meaning of `/exit`.
- **Args are split off but ignored**. Step 7's commands are all parameter-less. Step 8's `/plan` and Step 12's `/remember <fact>` will parse the args portion — the shape is already there.

### Shared context

`SlashContext` has two halves:

```csharp
public sealed class SlashContext
{
    // Mutable session state (used by /clear).
    public string SessionId { get; set; } = "";
    public AgentSession Session { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public string? Preview { get; set; }

    // Read-only references.
    public AIAgent Agent { get; init; } = null!;
    public string Model { get; init; } = "";
    public AgentConfig? Config { get; init; }
    public UsageAccumulator SessionUsage { get; init; } = null!;
    public ApprovalState Approval { get; init; } = null!;
}
```

The mutable half is what makes `/clear` work. The new session it mints replaces the current one — *in place* — so when ChatLoop's outer loop iterates again, it's already using the new state. The alternative was a discriminated-union return type. Mutation is workshop-pragmatic; not threadsafe but also not concurrent.

### /help's chicken-and-egg

`HelpCommand` lists every command in the registry, *including itself*. The registry doesn't exist when its commands are being constructed. Cleanly resolved with a captured-lazy closure:

```csharp
public static SlashRegistry Default()
{
    SlashRegistry? self = null;
    var commands = new List<ISlashCommand>
    {
        new ExitCommand("/exit",  "..."),
        new HelpCommand(() => self!.All),   // resolves at call time, not now
        // ... other commands ...
    };
    self = new SlashRegistry(commands);
    return self;
}
```

The `Func<IReadOnlyList<ISlashCommand>>` is evaluated only when /help runs — long after `self` is non-null. Cheaper than a static field, kinder to tests, and a workshop-friendly demonstration of "init order has two phases: capture-the-thunk now, resolve-the-thunk later."

### The yolo bypass + "always approve" memory

*In [`Harness/Commands/ApprovalState.cs`](../Harness/Commands/ApprovalState.cs).*

```csharp
public sealed class ApprovalState
{
    public bool YoloMode { get; set; }
    public HashSet<string> AlwaysApprove { get; } = new(StringComparer.Ordinal);
}
```

Two mechanisms, one struct:

- **`YoloMode`** is the blanket bypass. `/yolo` flips it. When on, every approval-required tool auto-approves with `[auto-approved (yolo)]`.
- **`AlwaysApprove`** is per-tool memory. Populated when the user types `a`/`always` at the prompt. Future calls to that specific tool auto-approve with `[auto-approved (always)]`. Other tools still prompt.

*In [`Harness/ApprovalPrompt.cs`](../Harness/ApprovalPrompt.cs):*

```csharp
public static bool Ask(ToolApprovalRequestContent request, ApprovalState state)
{
    var toolName = request.ToolCall is FunctionCallContent fcc ? fcc.Name : null;
    var label = /* render call */;

    // Pre-prompt fast paths.
    if (state.YoloMode) {
        Console.WriteLine($"  approve {label}? [auto-approved (yolo)]");
        return true;
    }
    if (toolName is not null && state.AlwaysApprove.Contains(toolName)) {
        Console.WriteLine($"  approve {label}? [auto-approved (always)]");
        return true;
    }

    // ...regular prompt; if answer is 'a'/'always', add to set and approve...
}
```

The state is passed in — **not a singleton**. Tests construct an `ApprovalState` per case and drive it. `/yolo` flips the same instance the prompt is consulting. **No global mutable state in the production path.**

`AlwaysApprove` is intentionally **process-local**. No disk persistence — your `/yolo` choices don't bleed into the next session. The temptation to store "always approve this tool" on disk is real, but it belongs with Step 12's `FileMemoryProvider`, where storing user preferences is the whole point.

### Per-session vs per-turn usage

The old `TurnUsage` class is now used in two scopes:

```csharp
// In ChatLoop.RunAsync, top of the loop:
var sessionUsage = new UsageAccumulator();   // lives the whole session

// Inside one user input's processing:
var turnUsage = new UsageAccumulator();      // reset every turn

// In the streaming switch:
case UsageContent uc:
    turnUsage.Add(uc.Details);
    sessionUsage.Add(uc.Details);
    break;
```

The rename `TurnUsage → UsageAccumulator` is a tiny but conceptually load-bearing change. **The name `TurnUsage` lied once we used the same class for session totals.** Renaming costs almost nothing today; not renaming would cost a small amount of cognitive overhead every time someone read `new TurnUsage()` and asked "is this per turn or per session?"

`/cost` reads `sessionUsage`, formats with `label: "session"`. The per-turn line after each reply still uses `turnUsage`, formats with the default `label: "turn"`. **One class, two uses, clearly labelled.**

### ChatLoop's main loop is now small

The Step 5 ChatLoop had ~30 lines of inline command handling. Step 7 boils that down to:

```csharp
var slashResult = registry.TryDispatch(input.TrimEnd(), ctx);
if (slashResult == SlashAction.Exit) break;
if (slashResult == SlashAction.Continue) continue;

// ...rest of the turn loop runs only if input wasn't a slash command...
```

Adding a slash command in Step 8/9/12 is now "one class in `SlashDispatch.cs`," not "an if-branch + state plumbing in ChatLoop." That's the win.

## Verify

```bash
dotnet build
dotnet test                 # 117 unit tests (99 prior + 18 new across SlashRegistry, ApprovalPrompt updates, UsageAccumulator label)
```

Live:

```bash
dotnet run
you > /help
# Expect: sorted list of all 10 commands with descriptions.

you > /tools
# Expect: 7 tools, three marked [approval].

you > one word reply: hello
claude > hello.
(turn: 2035 in + 4 out, $0.0021)

you > /cost
(session: 2035 in + 4 out, $0.0021)

you > /yolo
(yolo: ON ...)

you > use bash to print "yolo"
claude > 
  approve bash(command="echo yolo")? [auto-approved (yolo)]
[bash: command="echo yolo"]
yolo

you > /yolo
(yolo: OFF ...)

you > use bash to print "world"
claude > 
  approve bash(command="echo world")? [y/N/a=always]: a
  → approved (and remembering 'bash' for the rest of this session)
[bash: command="echo world"]
world

you > use bash again to print "again"
claude > 
  approve bash(command="echo again")? [auto-approved (always)]
[bash: command="echo again"]
again
```

The combination of `/yolo` for a session-wide bypass and `a` for per-tool memory covers most of the "approval friction" complaints without losing the safety property that fresh tools always prompt the first time.

## Pitfalls

### Slash-command parser is intentionally dumb

We split on the first space and ignore everything after. That's fine for Step 7's commands (all parameter-less) but Step 12's `/remember <fact>` will need to parse arguments. The shape's there — `input[firstSpace+1..]` is the args portion — but the parser stays trivial until something demands more.

### `/clear` calls an async method synchronously

`ICommand.Run` is sync — adding `async` ripples through everywhere. `/clear` needs `agent.CreateSessionAsync()`. We bridge with `.AsTask().GetAwaiter().GetResult()`. This is a deadlock risk in ASP.NET / WPF contexts; safe here because we're a console app with no synchronization context. **Don't copy this pattern into a UI framework.** Stretch: make `ISlashCommand.Run` async.

### Approval state is process-local

`AlwaysApprove` and `YoloMode` are forgotten on every restart. Some users will want "always approve this tool, always" on disk. Step 12's `FileMemoryProvider` is the right place; bolting it into Step 7 would be feature creep.

### `/yolo` is dangerous

A blanket auto-approve for every tool call, including `bash`. The yolo prompt explicitly warns ("every approval-required tool will auto-approve"), but the user has to read it. **Don't ship a binary with `--yolo-on-startup` baked in.** No env var that sets it. The toggle has to be a deliberate, in-session decision.

### `/help` rebuilds the closure every call

`HelpCommand` captures `() => self!.All`. Every `/help` invocation re-enumerates the dictionary, allocates the sorted list. Cheap; not worth caching. **Workshop-correct** to write the simple form first and measure before optimizing.

### Renaming `TurnUsage → UsageAccumulator` was a real teaching moment

The class was fine; the name was wrong as soon as Step 7 needed session-level totals. **A name that lies costs you cognitive cycles every time you read it.** Rename when usage drifts beyond the original framing. Tests update mechanically.

### `/sessions` doesn't paginate

If you have 200+ sessions the list dumps all of them. The `--list` from Step 0 has the same property. Stretch exercise.

## Stretch exercises

- **`/help <command>`** — show the detailed description and any args for a specific command.
- **Aliases as a first-class concept.** Right now `/quit` is a separate `ExitCommand` instance. A real `Aliases` collection on `ISlashCommand` lets one class register multiple names.
- **`/forget <tool>`** to remove a tool from `AlwaysApprove`. Pairs with `a` at the prompt.
- **`/resume <prefix>`** in-chat (companion to `/sessions`) so you don't have to `/exit` then `dotnet run -- --resume <id>`.
- **Tab completion** of `/` commands. `System.CommandLine` or hand-rolled with readline.
- **A `/cost` flag** (`/cost since-start` vs `/cost last-turn`) once two scopes start mattering.
- **Persist `YoloMode` to disk** behind a hidden flag, so CI runs can set it via file rather than env. Lives between Step 7 and Step 12.

## Where the seams are

What this step deliberately doesn't have:

- **No tab completion.** Stretch.
- **No `/help <command>` detail view.** Single-line descriptions only.
- **No async commands.** `ISlashCommand.Run` is sync; `/clear` bridges. Stretch.
- **No persistence of `YoloMode` / `AlwaysApprove`.** Step 12's territory.
- **No `--yolo` startup flag.** By design — yolo should be a deliberate in-session toggle.
- **No `/plan`, `/accept-plan`, `/interrupt`.** Steps 8 and 9.

## Next

→ **Step 08 — Plan mode (read-only tool subset)** *(planned)*

Up next: a temporary read-only mode the model enters when asked to "plan" something. The user sees the plan, approves it (or doesn't), then mutation tools become available for the execution phase. A way to *think before doing*.
