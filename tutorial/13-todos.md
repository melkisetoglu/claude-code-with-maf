# Step 13 — Todo tracking via `TodoProvider`

> *Goal: when the user asks for multi-step work, the model plans it as a checklist, marks items off as it goes, and the list survives across turns, compaction, and full session resume. `/todos` lets the user glance at the current state without burning a turn.*

Step 10 stopped sessions falling off the context window. Steps 11–12 gave the agent project-context awareness and persistent notes. Step 13 fills the last gap in Milestone 5: **structured in-flight state for multi-step work**. Plain prose in chat history is fragile (compaction can drop it); free-form memory notes don't have a *schema*. Todos give the agent a checklist data structure the framework treats as first-class, injected into every system prompt and persisted in the session bag.

It's also the workshop's **fourth `AIContextProvider`** and the **first one whose harness UX has to call into the provider's C# API directly** — which forces a small but interesting refactor of `AgentBuilder.Build()`'s return shape.

## What you'll have at the end

```text
you > I want to add a CHANGELOG.md. Plan this as a todo list, don't execute.
claude > [load_skill: skillName="repo-context"]      ← Step 11
         [FileMemory_ReadFile: fileName="user_preferences.md"]  ← Step 12
         [list_dir: path="."]                        ← Step 1
         [TodoList_Add: todos="[{...},{...},{...},{...},{...}]"]
         Here's the 5-step plan: ...

you > /todos
Todos (0/5 done):
  ☐ [1] Examine repo structure and current git history
        Check TUTORIAL.md for step info, review git tags...
  ☐ [2] Decide CHANGELOG format and scope
  ☐ [3] Extract initial changelog entries
  ☐ [4] Create CHANGELOG.md file
  ☐ [5] Commit with [doc] prefix

you > /exit
$ dotnet run -- --resume <id>
you > /todos
Todos (0/5 done):   ← survives the restart, same items
  ☐ [1] ...
```

The model autonomously chained four previous-step features (skills + memory + list_dir + the new todos) into one planning turn. **Steps 11–13 compose** — that's the milestone payoff.

## MAF concepts introduced

### 1. `TodoProvider` — simpler than its cousins

```csharp
new TodoProvider(TodoProviderOptions options)
```

**One argument.** No `AgentFileStore`, no per-session state-accessor lambda. State lives in memory + session bag. The provider exposes two read methods we can call from the harness:

- `GetAllTodosAsync(AgentSession)` → `IReadOnlyList<TodoItem>` — full list including completed
- `GetRemainingTodosAsync(AgentSession)` → `List<TodoItem>` — incomplete only

### 2. `TodoProviderOptions`

| Property | Type | What it does |
|---|---|---|
| `Instructions` | `string` | Injected into the system prompt (familiar pattern). |
| `SuppressTodoListMessage` | `bool` | Default `false`. When `false`, the framework injects the current list into the system prompt every turn — the whole point. Set `true` only if you want to manage injection yourself. |
| `TodoListMessageBuilder` | `Func<…>` | Custom formatter for how the list appears in-prompt. Default is fine for the workshop. |

### 3. `TodoItem` schema

```csharp
class TodoItem
{
    int    Id          { get; set; }
    string Title       { get; set; }
    string Description { get; set; }
    bool   IsComplete  { get; set; }
}
```

**Binary completion.** No `pending` / `in_progress` / `completed` enum. Claude Code's surface shows three states; MAF gives two. "Currently working on it" is a UI inference (e.g., the first incomplete item) — not a stored state.

### 4. Auto-registered tools (5)

The probe-first smoke turned up:

```text
TodoList_Add          — create one or more items
TodoList_Complete     — flip IsComplete = true on an id
TodoList_Remove       — delete an item by id
TodoList_GetAll       — read all items
TodoList_GetRemaining — read incomplete items only
```

Note the naming: `TodoList_*` (not `Todo_*`). Each provider has its own naming style — `FileMemory_*`, `FileAccess_*`, `TodoList_*`. Don't pin tests on exact tool names; pin on behavior. There's no built-in "update" tool — to revise an item, the model removes + re-adds, or just completes the old one and adds a new one.

### 5. State-bag key

After a turn, the saved session JSON's `stateBag` now contains:

```json
{
  "toolApprovalState": { ... },
  "FileMemoryProvider": { ... },
  "InMemoryChatHistoryProvider": { ... },
  "TodoProvider": { ... },          ← new
  "ClaudeChat.Compaction": { ... }
}
```

Framework-managed key. No `stateKey` parameter to set, no constant to pin in code — though `TodoRegistrationTests` does assert that *some* state key contains `"Todo"` so a future rename doesn't silently break resume.

## Code walkthrough

### Wire the provider — `Agent/AgentBuilder.cs`

```csharp
#pragma warning disable MAAI001
var todoProvider = new TodoProvider(new TodoProviderOptions
{
    Instructions =
        "When the user asks for multi-step work, plan it as a todo list FIRST: " +
        "add an item per major step with TodoList_Add, then work through them, " +
        "calling TodoList_Complete as you finish each. " +
        "The user can see your current list at any time with /todos.",
});
#pragma warning restore MAAI001
providers.Add(todoProvider);
```

Three deliberate choices:

- **Always on** — no `Directory.Exists(...)` gate the way `./skills/` and `./memory/` use. The provider needs no disk state and no setup, so opt-in by *running this version* is enough.
- **Imperative instructions** — explicit about *when* to use the list ("for multi-step work") and *how* the user observes it ("with `/todos`"). Soft constraint in the prompt; the auto-registered tools are the hard mechanism.
- **Default `SuppressTodoListMessage = false`** — let the framework inject. The workshop's whole reason for the provider is the auto-injection. `TodoRegistrationTests` pins this default so a future MAF change can't silently flip it.

### The new return shape — `AgentBuilder.BuildResult`

This step introduces the first provider whose **read-side API** the harness needs to call. `/skills` and `/memory` mirror disk state and don't touch the provider object after construction. `/todos` calls `provider.GetAllTodosAsync(session)` — we need a reference.

```csharp
public sealed record BuildResult(AIAgent Agent, TodoProvider Todos);

public static BuildResult Build(...)
{
    // ...build everything...
    return new BuildResult(wrapped, todoProvider);
}
```

`Program.cs` unpacks and forwards:

```csharp
AgentBuilder.BuildResult built = AgentBuilder.Build(...);
AIAgent agent = built.Agent;
// ...
await ChatLoop.RunAsync(agent, model, ..., agentConfig, built.Todos);
```

`ChatLoop.RunAsync` puts the provider into `SlashContext.Todos`. Now `TodosCommand` can reach it.

Future providers with harness-relevant read APIs go into `BuildResult` too — keep it minimal but explicit (no static caches, no service locator).

### The slash command — `Harness/Commands/SlashDispatch.cs`

```csharp
internal sealed class TodosCommand : ISlashCommand
{
    public string Name => "/todos";
    public string Description => "List the agent's todo items (✓ done, ☐ pending) from TodoProvider";

    public SlashAction Run(SlashContext ctx)
    {
        IReadOnlyList<TodoItem> items;
        try
        {
            items = ctx.Todos.GetAllTodosAsync(ctx.Session).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"(error reading todos: {ex.GetType().Name}: {ex.Message})");
            return SlashAction.Continue;
        }

        // ...print "Todos (N/M done):" header, then each item with ✓/☐ marker
        //    + Title (line) + Description (indented continuation line)
    }
}
```

Two ergonomic touches worth pointing at:

- **`✓` / `☐` markers** are picked to be distinct in both light and dark terminals and to round-trip safely through Unicode (no terminal I've tested mangles them). Plain ASCII fallback (`[x]` / `[ ]`) is a stretch if you have to support a terminal that does.
- **`N/M done` header** gives the user a glance-level sense of progress without scanning the list. The model's binary `IsComplete` makes this trivial.

The command shows **all** items (completed + pending), not just remaining — completed work is useful context for "what just happened in this session."

### `SlashContext` gains a field

```csharp
public sealed class SlashContext
{
    // ...existing fields...

#pragma warning disable MAAI001
    public TodoProvider Todos { get; init; } = null!;
#pragma warning restore MAAI001
}
```

`null!` default keeps existing tests (which construct contexts without a provider) compiling without changes. The `/todos` command's `try/catch` handles `null!` gracefully — a unit test pins that the dispatcher doesn't crash when `Todos` is unwired.

## Verify

```bash
dotnet build && dotnet test --filter Category=Unit    # 153 tests pass
```

Then a multi-step task to see the integration end-to-end:

```text
$ dotnet run
you > I want to add a CHANGELOG.md. Plan this as a todo list, don't execute.
claude > [TodoList_Add: todos="[{...5 items...}]"]
         Here's the plan...

you > /todos
Todos (0/5 done):
  ☐ [1] Examine repo structure and current git history
  ...
```

Restart and confirm persistence:

```text
$ dotnet run -- --resume <id>
you > /todos
Todos (0/5 done):     ← all 5 items back, ids preserved
```

And the state-bag check from disk:

```bash
$ cat sessions/<id>.json | jq '.session.stateBag | keys'
[
  "ClaudeChat.Compaction",
  "FileMemoryProvider",
  "InMemoryChatHistoryProvider",
  "TodoProvider",       ← new
  "toolApprovalState"
]
```

The model also auto-uses the list when relevant: ask it "what's left to do?" and it'll inspect its own injected list and answer without calling any tool. The framework injects the list every turn so the model always has the current state.

## Pitfalls

### Binary completion only

`TodoItem.IsComplete` is a bool. There's no `pending` / `in_progress` / `completed` enum. If you want to show "currently working on" in a UI, derive it (e.g., "the first incomplete item is in-progress while the model is actively running"). Setting `IsComplete = true` halfway through and back to `false` would model an aborted item — workable but not what the field is for; use `TodoList_Remove` + `TodoList_Add` for actual revisions.

### `Build()`'s return shape changed — Program.cs and ChatLoop.cs ripple

This is the first time since Step 0 cleanup that `Build()`'s signature changed. The ripple is two files:

- `Program.cs` unpacks `built.Agent` instead of using the return directly.
- `ChatLoop.RunAsync` gains a `TodoProvider todos` parameter.

Worth flagging because future provider steps will follow the same recipe: if the provider has a read-side C# API the harness wants, it goes into `BuildResult`. Resist the urge to use a static cache — that hides the dependency and breaks parallel-test isolation. Explicit ripple is the cost of honesty.

### Sync-over-async in the slash command

The `ISlashCommand.Run` contract is sync (`SlashAction Run(SlashContext ctx)`), but `GetAllTodosAsync` is async. We call `.GetAwaiter().GetResult()`. This is **only safe** because the call is in-memory and IO-free — no thread-pool deadlock risk. If a future MAF version moves todo storage to async backends (vector DB, remote service), this needs refactoring to async-all-the-way-down (which would mean the slash registry's `Run` becomes async too — a real change).

Pinned by an explicit comment in `TodosCommand.Run`.

### Tool naming style varies between providers

Three providers, three styles:

| Provider | Auto-tool naming |
|---|---|
| `AgentSkillsProvider` | `load_skill` (lowercase) |
| `FileMemoryProvider` | `FileMemory_SaveFile` (PascalCase + underscore mix) |
| `TodoProvider` | `TodoList_Add` (PascalCase + underscore mix) |

The framework didn't unify these. Don't write tests that pin exact tool names — pin behavior. If you write user-facing prose that mentions a tool name, treat the names as preview-quality (likely to be renamed in a future MAF release).

### Process-globals tests need a shared `[Collection]` (still)

`TodosCommandTests` only swaps `Console.Out` (no cwd), so the existing `Console-shared-static` collection is the right fit. Nothing new here — but if you add a Step 13-style command that touches cwd, remember the lesson Steps 11–12 documented.

### MAF API drift watch

`TodoProvider`, `TodoProviderOptions`, `TodoItem`, plus the five auto-registered tools — all `[Experimental]` (MAAI001). The pragma's narrow at each reference site so a rename or relocation in a future preview reintroduces the warning and flags the migration.

## Stretch

- **Override `TodoListMessageBuilder`** to change how the list appears in the system prompt. Compare token cost of the default vs a more compact format.
- **A `/todos clear` mutating slash command** that calls `TodoList_Remove` for every item id. Today the model handles list mutation; a user-driven escape hatch would be friendly when the model gets confused.
- **Status-line render of the todo list above the prompt** — like a tmux status bar. Updates each turn. Visible without typing `/todos`.
- **Wire `/plan` mode tighter to todos.** Currently plan mode just prefixes "[plan mode: read-only — produce a plan]" to user messages; nothing forces the *form* of the plan. A tighter coupling: in plan mode the model can only call `TodoList_Add` (other mutators auto-denied), so the plan IS the todo list, structured by construction.
- **Per-item timing.** Wrap `TodoList_Complete` calls to record elapsed time; surface `(N/M done — last 3 took 4m12s)` in `/todos`. Useful for "is the model stuck?" diagnostics.
- **Optional approval gate on `TodoList_Remove`.** Adds + completes are safe; removes can erase plans the user wanted to keep. Wrap just `TodoList_Remove` in `ApprovalRequiredAIFunction` after the provider builds.
- **Export todos to disk.** A `/todos export` that writes the current list to a Markdown file the user can paste into a tracker.

## Where the seams are

- `TodoProvider` is the **fourth `AIContextProvider`** in the workshop, after Compaction, Skills, and Memory. The pattern (attach via `options.AIContextProviders`, framework auto-registers tools, state persists in session bag) is now solid enough to apply prophylactically to the remaining Milestone-6 providers (`SubAgentsProvider`, `TextSearchProvider`, etc.).
- **`AgentBuilder.BuildResult` is the new harness/provider boundary.** Any future provider whose read-side C# API the harness needs to call goes into the record. Resist static state.
- **Plan mode (Step 8) becomes more honest with todos available.** Step 8's user-message prefix is a soft constraint; the model could theoretically still mutate things. With `TodoList_Add` available, plan mode's natural artifact is a populated todo list. A tighter integration is a stretch above, but even the loose composition we have now is more concrete than free-prose planning.
- **Three provider-auto-registered tools** (`load_skill`, the 5 `FileMemory_*`, the 5 `TodoList_*`) all bypass `ToolApprovalAgent` by default. That's the framework's choice and it's the right default — these are provider-internal mechanisms, not user-facing mutators. The Step 4 mutators (`write_file`, `edit_file`, `bash`) and any future workshop-original tool are the gated ones. The line is **"workshop-original means approval-required; framework-auto means trust the provider's design."**

## Next

→ **Milestone 6 — Power features**: hooks/middleware, sub-agents, MCP integration, budgets & circuit breakers. Steps 14–17. The probe-first methodology continues — Step 14's first action will be probing what `AgentModeProvider` (visible in the reflection probe) auto-registers, since it sounds plan-mode-adjacent and might let us simplify Step 8 retrospectively.
