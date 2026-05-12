# Step 12 — Cross-session memory via `FileMemoryProvider`

> *Goal: notes the model writes in one session survive into the next. Tell the agent "I prefer terse answers, no marketing speak," exit, restart, ask "what do you remember about me?" — the model reads back what it wrote yesterday.*

Steps 0–11 gave the agent everything it needs *within* a session: tools, an approval gate, compaction, skill awareness. But every session still starts cold from the agent's point of view. The `/sessions` list is for humans to navigate; the model itself has no continuous identity across runs.

Step 12 closes that gap with `FileMemoryProvider` — the workshop's **third `AIContextProvider`**. The provider gives the agent a persistent scratchpad folder (`./memory/`), backed by `FileSystemAgentFileStore`. The model writes notes there; on the next session, the framework injects the index into the system prompt and exposes file tools so the model can read its old notes on demand.

This step is also the first where the **probe-live-first methodology from Step 11's pitfall** paid off prophylactically: I attached the provider minimally, asked the model to list its tools, saw 5 new ones, and stopped right there — instead of writing a redundant `read_memory` tool the way I almost did with `read_skill`.

## What you'll have at the end

```text
$ mkdir memory                # opt in
$ dotnet run

you > Save a note that I prefer terse, direct answers with no marketing speak.
claude > [FileMemory_SaveFile: fileName="user_preferences.md", content="...",
          description="User communication preferences and style guidelines"]
Done. Your preferences are saved.

you > /exit

$ ls memory/
memories.md                          # framework-maintained index
user_preferences.md                  # the note
user_preferences_description.md      # framework-maintained sidecar

$ dotnet run                         # NEW session, no shared history
you > What do you remember about me?
claude > I'll read my memory file to check what I know about your preferences.
[FileMemory_ReadFile: fileName="user_preferences.md"]
Got it! I remember you prefer:
- Terse, direct answers — no fluff
- ...
```

The model wrote without us asking how. The model read in a new session without us telling it where. **Zero workshop tool code** — the framework's `FileMemoryProvider` auto-registers everything.

If `./memory/` doesn't exist, the provider isn't attached — Step 12 is additive.

## MAF concepts introduced

### 1. `AgentFileStore` — the storage abstraction

| Type | What it does |
|---|---|
| `AgentFileStore` (abstract) | 7-method storage interface: `ReadFileAsync`, `WriteFileAsync`, `DeleteFileAsync`, `FileExistsAsync`, `ListFilesAsync`, `SearchFilesAsync`, `CreateDirectoryAsync`. All paths are relative to the store's root. |
| `FileSystemAgentFileStore(rootPath)` | Concrete impl backed by a real OS folder. We use this — persistence is the whole point. |
| `InMemoryAgentFileStore()` | Concrete impl backed by RAM. Useful for tests; defeats persistence in production. |

The store is a separable seam: you could write your own implementation backed by S3, an encrypted blob, or a database, and `FileMemoryProvider` would happily use it.

### 2. `FileMemoryProvider` — the context provider

```csharp
new FileMemoryProvider(
    AgentFileStore fileStore,
    Func<AgentSession, FileMemoryState> stateAccessor,
    FileMemoryProviderOptions options)
```

Three pieces:

- **`fileStore`** — where bytes live. `FileSystemAgentFileStore("./memory")`.
- **`stateAccessor`** — given a session, return its `FileMemoryState`. The only meaningful field is `WorkingFolder` — the subpath within the store that this session sees as "its memory." Return the same value for every session and you have **shared cross-session memory**; return `$"session-{session.Id}"` and you have **per-session memory** (each session writes to its own subfolder). We pick global.
- **`options.Instructions`** — a string injected into the system prompt telling the model about its memory. The framework appends the working-folder details automatically.

### 3. Auto-registered tools — the framework does the wiring

This is what made Step 11 confusing. The provider attaches as an `AIContextProvider`, but it also **registers 5 callable tools** the model uses to manipulate the store:

```text
FileMemory_SaveFile(fileName, content, description)
FileMemory_ReadFile(fileName)
FileMemory_DeleteFile(fileName)
FileMemory_ListFiles()
FileMemory_SearchFiles(query)
```

The model sees these in its tool schema alongside `read_file`, `bash`, etc. **We register zero of them.** Same pattern as Step 11's `load_skill`: attach the provider, ask the model "list every tool," and you find the framework's contribution.

### 4. The auto-maintained index — `memories.md`

When the model calls `FileMemory_SaveFile(fileName, content, description)`, the framework writes **three** files:

- `<fileName>` — the content
- `<fileName>_description.md` — the description as a separate file
- `memories.md` — an index containing one bullet per saved memo, pointing at the description

```text
$ cat memory/memories.md
# Memory Index

- **user_preferences.md**: User communication preferences and style guidelines
- **debugging_notes.md**: Notes on the Step 9 streaming polish bug
```

On the next session, the framework injects this index into the system prompt so the model can *see* what it has saved before deciding to read any specific file. Progressive disclosure again: cheap glance, rich read.

### 5. The state bag entry

After a turn, the saved session JSON's `stateBag` now contains:

```json
{
  "toolApprovalState": { ... },
  "FileMemoryProvider": { ... },
  "InMemoryChatHistoryProvider": { ... },
  "ClaudeChat.Compaction": { ... }
}
```

The framework manages the `"FileMemoryProvider"` key automatically — no `stateKey` parameter for us to set.

## Code walkthrough

### Wire the provider — `Agent/AgentBuilder.cs`

```csharp
public const string MemoryDirectoryName = "memory";
```

The block, inserted right after the skills provider so the `AIContextProviders` list grows naturally:

```csharp
var memoryDir = Path.Combine(Directory.GetCurrentDirectory(), MemoryDirectoryName);
if (Directory.Exists(memoryDir))
{
#pragma warning disable MAAI001
    var memoryStore = new FileSystemAgentFileStore(memoryDir);
    var memoryProvider = new FileMemoryProvider(
        memoryStore,
        _ => new FileMemoryState { WorkingFolder = "" },
        new FileMemoryProviderOptions
        {
            Instructions =
                "You have a persistent memory folder available across all sessions of this agent. " +
                "Use the file tools the memory provider gives you to save facts, decisions, and " +
                "conventions you should remember the next time you run. Memory writes are silent " +
                "(no approval gate) — use them freely for notes; use write_file (approval-gated) " +
                "for changes to the user's actual project files.",
        });
#pragma warning restore MAAI001
    providers.Add(memoryProvider);
}
```

Four deliberate choices:

- **`WorkingFolder = ""`** — empty string means "use the entire store." Every session sees the same folder. Switching to `$"session-{session.Id}"` would partition memory per-session (a useful stretch for multi-user setups).
- **Opt-in by directory existence.** No `./memory/` ⇒ no provider attached. Same pattern as `./skills/` and `agent.json` discovery.
- **`Instructions` describe the *purpose*, not the mechanics.** The framework auto-injects info about the working folder and tool availability — telling the model *what to use it for* is the part we contribute.
- **`#pragma warning disable MAAI001` narrowly.** `FileMemoryProvider`, `FileMemoryProviderOptions`, `FileMemoryState`, and `FileSystemAgentFileStore` are all `[Experimental]`. Suppress here, not project-wide, so a future MAF rename reintroduces the warning.

### Add the slash command — `Harness/Commands/SlashDispatch.cs`

Same diagnostic shape as `/skills`. Lists everything under `./memory/` — including the framework's bookkeeping files (`memories.md`, `*_description.md`), because hiding them would lie about the state of the memory.

```csharp
internal sealed class MemoryCommand : ISlashCommand
{
    public string Name => "/memory";
    public string Description => "List files written by FileMemoryProvider under ./memory/";
    // ...same opt-in/empty/listing shape as SkillsCommand...
}
```

### `.gitignore` — exclude `memory/`

```gitignore
# User data — the model writes cross-session notes here via
# FileMemoryProvider (Step 12). Personal, possibly sensitive. Opt-in by
# creating the directory — ignored once it exists.
memory/
```

The memory directory is gitignored for the same reason `sessions/` is: it contains stuff the model wrote *about the user*, not workshop source. The opt-in-by-existence pattern means committing `memory/.gitkeep` to make the dir present would be wrong — the file's presence would auto-enable the provider in everyone's clone.

## Verify

```bash
dotnet build && dotnet test --filter Category=Unit    # 148 tests pass
```

The interesting verification is live, and it has three parts:

**Part 1: probe the auto-registered tools.**

```text
$ mkdir memory && dotnet run
you > List every tool name available to you, one per line.
claude > read_file
list_dir
glob
grep
write_file
edit_file
bash
load_skill
FileMemory_SaveFile           ← from FileMemoryProvider
FileMemory_ReadFile
FileMemory_DeleteFile
FileMemory_ListFiles
FileMemory_SearchFiles
```

**Part 2: write something the model wouldn't otherwise know.**

```text
you > Save a note that I prefer terse, direct answers with no marketing speak.
claude > [FileMemory_SaveFile: fileName="user_preferences.md", ...]
```

Inspect the disk:

```text
$ ls memory/
memories.md
user_preferences.md
user_preferences_description.md

$ cat memory/memories.md
# Memory Index
- **user_preferences.md**: User communication preferences and style guidelines
```

**Part 3: cross-session read.** Exit, restart with a fresh session, ask:

```text
$ dotnet run
you > What do you remember about me?
claude > I'll read my memory file...
[FileMemory_ReadFile: fileName="user_preferences.md"]
Got it! I remember you prefer:
- Terse, direct answers — no fluff
- ...
```

The new session has no shared chat history with the previous one, but it knows about `user_preferences.md` because the framework injected the `memories.md` index, and it knows how to read it because `FileMemory_ReadFile` is in its tool schema.

Check the saved session JSON for the state-bag entry:

```bash
$ cat sessions/<id>.json | jq '.session.stateBag | keys'
[
  "ClaudeChat.Compaction",
  "FileMemoryProvider",
  "InMemoryChatHistoryProvider",
  "toolApprovalState"
]
```

## Pitfalls

### The probe-first methodology actually worked

This is Step 11's lesson, applied prophylactically. Before writing any C#, I would have been tempted to write a `Tools/MemoryNote.cs` with save/read functions — same trap as `read_skill`. Instead I:

1. Wrote the **minimal** provider wiring (just attach `FileMemoryProvider`, nothing else).
2. Ran `dotnet run`, asked the model "list every tool name available to you."
3. Saw five `FileMemory_*` tools I didn't register.
4. Stopped. Step 12 turns out to have zero tool code.

**The cost of the probe**: one `dotnet run`, one prompt, ten seconds. **The cost of not doing the probe**: in Step 11, ~150 LOC of redundant `read_skill` plumbing that I had to delete. Easy decision.

### The framework writes more files than you save

`FileMemory_SaveFile(fileName: "x.md", content, description)` produces **three** files: `x.md`, `x_description.md`, and `memories.md` (the index). The first time you peek at `./memory/` and see three files when the model "saved one," it's confusing. The chapter calls this out before users assume the framework is bugged.

### `WorkingFolder = ""` is the global-memory key

`FileMemoryState.WorkingFolder` is *relative to the store's root*. Empty string means "use the whole store" — every session sees the same files. If you set it to anything else (e.g., `$"session-{session.Id}"`), each session gets its own subfolder and they can't see each other's notes. That's a legitimate design (privacy in multi-user setups) but not "cross-session memory" in the workshop sense.

Pin the convention with a test (`Constant_pins_the_directory_name`) so a future "improvement" to per-session memory doesn't silently change the cross-session contract.

### Files have a UTF-8 BOM

Files the framework writes start with the UTF-8 BOM (`\xEF\xBB\xBF`). Most tools handle it transparently, but if you `cat memory/memories.md` you'll see a stray `﻿` glyph in some terminals. Not a bug — just a heads-up. If you eventually round-trip memory through tooling that's BOM-allergic, write a normalizer.

### Memory writes bypass the approval gate

Confirmed live: `FileMemory_SaveFile` does **not** route through `ToolApprovalAgent`. No `approve memory write?` prompt fires. This is the right default for an autonomous scratchpad — every memo would otherwise need user confirmation, defeating the UX. But it does mean a hostile prompt could fill `./memory/` with arbitrary text without your seeing it; relevant for shared-machine setups. The `Instructions` string we wrote makes the contract explicit: **memory is silent; `write_file` for user-visible work**.

### Process-globals tests need a shared `[Collection]`

`MemoryCommandTests` redirects `Directory.SetCurrentDirectory` in its ctor, same as `SkillsCommandTests` and `AgentConfigTests`. All three share `[Collection("Console-shared-static")]` so they serialize. The collection name's misleading by now (cwd ≠ Console), but a feature step is the wrong commit to rename it in.

### MAF API drift watch

`FileMemoryProvider`, `FileMemoryState`, `FileMemoryProviderOptions`, `FileSystemAgentFileStore`, and the `AgentFileStore` base are all `[Experimental]` (MAAI001). The pragma's narrow so a rename in a future preview reintroduces the warning. The bigger drift watch: the tool naming format `FileMemory_SaveFile` (PascalCase + underscore mix) feels provisional — wouldn't shock me if a future MAF normalizes to `save_memory_file` or similar. The slash command is name-agnostic; smoke tests aren't.

## Stretch

- **Per-session memory.** Set `WorkingFolder = $"session-{session.Id}"` for private per-session memory. Compose with global memory by attaching two `FileMemoryProvider`s.
- **Per-user memory.** Root the store at `Path.Combine(Environment.GetFolderPath(SpecialFolder.UserProfile), ".claudechat", "memory")` instead of cwd — memory spans every project the user works on.
- **agent.json field for the memory dir.** `memory: { dir: "..." }` mirroring the existing tools/instructions/model knobs.
- **Custom `AgentFileStore` backend.** S3, SQLite, encrypted-at-rest. The 7-method contract is small enough that this is genuinely doable in one sitting.
- **Search workflow.** `FileMemory_SearchFiles` is already auto-registered — write a chapter exercise that has the model build up enough memory across sessions that searching it becomes the cheap path.
- **A `/memory rm <name>` slash command.** Today the user can clean up via `rm memory/*` or trust the model with `FileMemory_DeleteFile`; an explicit slash command would be friendlier.
- **Make memory writes routable through approval optionally.** Bool in `agent.json`: `memory.requireApproval`. Wraps each `FileMemory_*` tool with `ApprovalRequiredAIFunction`. Useful for shared-machine setups.

## Where the seams are

- `FileMemoryProvider` is the **third of three** MAF context-provider types this workshop covers from the framework's catalog. Compaction (Step 10), Skills (Step 11), Memory (Step 12) — all attach via `options.AIContextProviders`, all persist state through `StateKeys` (when they have any), and **two of the three auto-register tools** (Skills → `load_skill`; Memory → 5 `FileMemory_*`). The pattern is solid enough now that **Step 13 (`TodoProvider`)** will follow the same recipe — probe its tool list first.
- The `AgentFileStore` abstraction is a clean seam to swap implementations without touching the provider. Custom-backend stretch above is essentially free.
- We picked `WorkingFolder = ""` (global) for cross-session memory. Per-session memory would be a different feature, and the same provider type supports both — the seam is just the lambda.
- Memory tools bypass the approval gate by framework choice. We could change that policy in our harness (wrap each in `ApprovalRequiredAIFunction` after the provider builds), but **soft-vs-hard constraints** (per Step 8) is the right frame: the Instructions string carries the soft "memory is silent" agreement; if the harness wants a hard gate, that's a deliberate harness decision, not a framework one.
- **`FileAccessProvider` is `FileMemoryProvider`'s cousin** — same `AgentFileStore`-based shape, same 5 auto-registered tools (named `FileAccess_*` instead of `FileMemory_*`), but no per-session `WorkingFolder` state. Verified live in a throwaway probe during this step: attaching `new FileAccessProvider(new FileSystemAgentFileStore(cwd), opts)` gives the model `FileAccess_ReadFile`, `_SaveFile`, `_ListFiles`, `_SearchFiles`, `_DeleteFile`. Which means **Steps 1–2's hand-written `read_file`/`list_dir`/`grep`/`write_file` could have been a one-line provider attach**. We didn't know that when we wrote them — and writing them taught the `AIFunction` shape that you still need for `edit_file`/`bash` (workshop originals, MAF doesn't ship those). But it's worth being explicit: a production agent on this stack would attach `FileAccessProvider` and only hand-roll the tools MAF doesn't cover. Step 1's chapter gets a retrospective heads-up note as a separate `[doc]` commit after this one.
- **The workshop-level rule that emerges from Steps 11–12.** Before writing any tool in any future step, run the "list every tool name available to you" smoke against a minimal wiring of whatever provider you're attaching. If MAF already ships the tool, the step becomes a configuration exercise; if MAF doesn't, the step writes one by hand. Either way the chapter is honest about which it is. This rule applies retroactively to Steps 1–4 and prophylactically to Steps 13–17.

## Next

→ [Step 13 — Todo tracking via `TodoProvider`](13-todos.md) *(planned)*: structured to-do state the model maintains across turns. Per the new methodology — probe its tool list first.
