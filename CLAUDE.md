# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

A **workshop-style tutorial** that grows a Claude Code-style console agent on top of **Microsoft Agent Framework (MAF)** in .NET 9, talking to Claude via the official Anthropic SDK adapter. The single project at the repo root grows step-by-step across 17 chapters in 6 milestones (see [TUTORIAL.md](TUTORIAL.md)).

Current state: **Step 15 — sub-agent delegation via `SubAgentsProvider`**. Fifth `AIContextProvider`. Wires ONE read-only "researcher" sub-agent (`AgentBuilder.ResearcherAgentName = "researcher"`) with `read_file` / `list_dir` / `glob` / `grep`, no mutation/bash, no providers attached. Same `AnthropicClient` and model as main. Framework auto-registers **6 tools** the main agent uses to delegate: `SubAgents_StartTask` (non-blocking), `_WaitForFirstCompletion`, `_GetTaskResults`, `_GetAllTasks`, `_ContinueTask` (multi-turn sub-agent conversations), `_ClearCompletedTask`. Async-by-design — not synchronous "delegate and wait". State in session bag under `"SubAgentsProvider"` + `"SubAgentsProvider_Runtime"` keys. New slash command `/agents` lists the configured researcher. **Required two csproj version pins to make sub-agent execution land**: an earlier draft hit `MissingMethodException` for `WebSearchToolResultContent.get_Results()` (a stale-API reference path in the Anthropic adapter driving `Microsoft.Extensions.AI.Abstractions`'s renamed type). Fixes: pin `Microsoft.Extensions.AI` + `.Abstractions` @ 10.5.1 (unifies the M.E.AI graph — three transitive versions had been resolving) AND pin `Anthropic` SDK @ 12.20.1 (the official "upgrade provider to 12.20.0+ to remove references to stale APIs" guidance from MAF). Both pins are now in csproj with the diagnosis trail in a comment. Sub-agent execution verified live end-to-end (1440ms wall-clock for a real researcher run; result piped back via `SubAgents_GetTaskResults`; main agent composes the answer). Tests: 7 new (4 SubAgentsRegistrationTests + 3 AgentsCommandTests); 160 unit tests total. Workshop-level lesson: when preview-drift gives you a `MissingMethodException`, reflect on the loaded DLL → compare to compiled IL expectations → check dep graph for multi-version splits → look for adjacent SDK upgrades. Reusable diagnosis pattern for future drift.

This means: when adding code, the unit of work is "the next step." Each step is one sitting, ends in a clean state, gets a git tag (`step-00`, `step-01`, …) and a `[step-NN]` commit prefix so `git log --oneline` reads like a table of contents. Don't sneak future-step features into the current step.

## Run / build

```bash
export ANTHROPIC_API_KEY=sk-ant-...

dotnet build
dotnet run                          # new session, prints its id
dotnet run -- --continue            # resume the most recent session
dotnet run -- --resume <id-prefix>  # prefix-match, like git/claude
dotnet run -- --list                # list past sessions
```

Short flags: `-c`, `-r`, `-l`, `-h`. In-chat (slash-prefixed): `/exit` / `/quit` / Ctrl+D, `/clear` (new session, previous one stays on disk), `/id`.

`ANTHROPIC_DEPLOYMENT_NAME` overrides the model (default `claude-haiku-4-5`; try `claude-sonnet-4-6` for harder questions).

## Tests

```bash
dotnet test                              # run all
dotnet test --filter Category=Unit       # unit tier (fast, no API key)
# dotnet test --filter Category=Live     # live tier (needs ANTHROPIC_API_KEY) — none yet
```

Two-tier strategy: **Unit** tier covers harness plumbing without hitting the API (SessionStore, ReadFile, eventually a fake-AIAgent ChatLoop test). **Live** tier hits real Claude — gated to nightly/dispatch in CI, never runs on every PR. No live tests exist yet; they land alongside an explicit testing-interlude step.

Tests live in [tests/ClaudeChat.Tests/](tests/ClaudeChat.Tests/). Each test class is tagged `[Trait("Category", "Unit")]`. **The main project's `.csproj` excludes `tests/**` from its compile glob** — without that, the SDK auto-includes test files into the main project and the build fails.

`SessionStore.Dir` is a settable static (was `const`) so tests can swap it for a temp directory. Production never reassigns it.

## Architecture (the bit that needs explaining)

The whole app is in [Program.cs](Program.cs) using top-level statements. Two layers compose:

1. **`AnthropicClient`** — raw HTTP client from the `Anthropic` SDK package.
2. **`AsAIAgent(...)`** — extension from `Microsoft.Agents.AI.Anthropic` that adapts the SDK client into a MAF **`AIAgent`**.

After `AsAIAgent()`, the code never speaks Anthropic-specific types again — everything is written against the MAF abstractions (`AIAgent`, `AgentSession`, `RunStreamingAsync`). That's the whole point: future steps add tools, approval gates, logging, compaction, sub-agents — all provider-agnostic.

### Session persistence — the metadata wrapper

MAF gives you `SerializeSessionAsync` → `JsonElement` and `DeserializeSessionAsync(JsonElement)` → `AgentSession`. That's the round-trip primitive; it tracks conversation state but knows nothing about ids, listings, or previews.

To get `--list` and prefix-resume, we wrap the framework blob with our own metadata:

```json
{
  "id": "a3f7c102",
  "createdAt": "...", "updatedAt": "...",
  "model": "claude-haiku-4-5",
  "preview": "first user message snippet",
  "session": { ...framework's opaque JsonElement... }
}
```

One file per session at `sessions/<id>.json`, written **after every turn** (so Ctrl+C can't lose state). Ids are 8 hex chars (4 random bytes); resume does prefix-match like `git checkout abc`. The `session` field is opaque — treat as a black box and hand it back to `DeserializeSessionAsync` untouched.

## Target folder structure

As steps land, **introduce each folder lazily the first time a step needs it** — don't pre-create empty ones. After Step 2, five code folders exist (`Agent/`, `Harness/`, `Persistence/`, `Tools/`, `tests/`) plus `tutorial/`; the rest will appear when their step does. Destination shape:

```
claude-code-with-maf/
├── Program.cs                     # entry point only: arg parsing + main loop
├── Agent/                         # building & wrapping the AIAgent
│   ├── AgentBuilder.cs            # AnthropicClient → AsAIAgent + providers/tools
│   └── Delegating/                # ToolApprovalAgent, LoggingAgent (steps 3, 5, 14)
├── Tools/                         # function tools (steps 1–4)
├── Providers/                     # MAF providers (steps 10–15)
├── Harness/                       # the "Claude Code feel" — NOT MAF
│   ├── ChatLoop.cs                # interactive chat loop, hands slash inputs to the registry
│   ├── ApprovalPrompt.cs          # y/N/a gate (step 3 + step 7 yolo/always)
│   └── Commands/                  # slash registry + every command (step 7)
│       ├── SlashDispatch.cs       #   ISlashCommand + SlashContext + SlashRegistry + commands
│       └── ApprovalState.cs       #   YoloMode + AlwaysApprove memory
├── Persistence/                   # session storage + metadata wrapper
├── Observability/                 # logging, tracing, pricing, token accumulator (step 5)
├── Config/                        # agent.json loader (step 6)
├── skills/                        # AgentSkillsProvider source dir (step 11)
│   └── <name>/SKILL.md            #   one folder per skill, YAML frontmatter + body
├── memory/                        # FileMemoryProvider scratchpad (step 12, gitignored)
│   ├── memories.md                #   framework-maintained index of saved memos
│   ├── <name>.md                  #   model-written notes (persist across sessions)
│   └── <name>_description.md      #   framework-maintained sidecars
├── tutorial/                      # one .md per step, filename matches step number
├── sessions/                      # gitignored runtime data (note: lowercase!)
├── claudechat.log                 # gitignored runtime log (step 5)
└── tests/                         # if/when added — separate .csproj
```

> **Naming note.** Code folder is `Persistence/`, runtime data folder is `sessions/`. They must not share a root word — macOS's case-insensitive filesystem makes git's gitignore evaluation case-insensitive too, so a `sessions/` ignore rule would also hide a `Sessions/` code folder. Keeping the names distinct avoids the trap.

Two boundaries worth holding:

- **`Program.cs` stays small.** Entry point only. Once `Agent/AgentBuilder.cs` exists, composition lives there and `Program.cs` just calls into it. Keeps each step's diff focused.
- **`Harness/` is separate from `Agent/`.** TUTORIAL.md frames it: "MAF is the runtime, not the UX." Anything in `Harness/` is code *you* write (slash commands, plan mode, streaming polish), not framework extension points. Don't mix the two folders.

Folders mirror MAF's own concept boundaries (`Tools`, `Providers`, delegating agents) so a reader checking out `step-12` can guess where the new file lives before opening it.

## MAF is preview — APIs drift

The package is `Microsoft.Agents.AI.Anthropic 1.5.0-preview.*`. **The published MS Learn samples have already drifted from the actual API in this version.** Known drifts:

| MS Learn sample | Actual API |
|---|---|
| `APIKey` | `ApiKey` |
| `AgentThread` | `AgentSession` |
| `agent.GetNewThread()` | `await agent.CreateSessionAsync()` |

**When something doesn't compile or a name doesn't exist, do not trust web docs — reflect on the restored DLLs to find the real names.** Expect more drift on each prerelease bump. If you find a new drift, add it to the table in [README.md](README.md).

## Tutorial chapter convention

Each step has a chapter in `tutorial/NN-name.md` with these sections, in order: **Goal**, **MAF concept**, **Code walkthrough**, **Verify**, **Pitfalls**, **Stretch**. Match that shape when adding new chapters.

## Voice — for any prose written here (README, TUTORIAL, chapters)

Honest over polished. Short and direct over elaborate. If it sounds like marketing copy, rewrite it. The repo's own framing — "in motion, not arrived" — applies: be explicit when something is preview/unfinished/not-yet-written rather than papering over it. (Workspace-level voice rules in [../CLAUDE.md](../CLAUDE.md) apply if present.)
