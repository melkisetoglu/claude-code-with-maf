# Claude Code with MAF

A workshop that grows a Claude Code-style console agent on top of **Microsoft Agent Framework** in .NET, one step at a time.

> **Following the workshop?** Read **[TUTORIAL.md](TUTORIAL.md)** for the step-by-step guide. This README is just "how to run it".

## Current state

The agent is **17 of 17 steps shipped** — a working Claude Code-style REPL on Claude, in C#, built on MAF. What it has:

**Chat & sessions**
- Streaming REPL on Claude via the official Anthropic SDK adapter.
- Named persistent sessions: `--list`, `--continue`, `--resume <id-prefix>` (git/claude-style prefix match).
- Session saved after every turn — Ctrl+C can't lose state.

**Tools**
- Read-only navigation: `read_file`, `list_dir`, `glob`, `grep`.
- Approval-gated mutations: `write_file`, `edit_file`, `bash`.
- Per-tool-call timing middleware prints `→ Nms` after every call.

**Observability**
- Per-turn token + cost reporting.
- JSON-Lines file logging (`claudechat.log`).
- Optional OpenTelemetry tracing (`--otel`).

**Configuration**
- External `agent.json` profile: model, system prompt, tool allowlist, approval rules, MCP servers.

**Slash commands** (16 total)
- `/help`, `/exit`, `/clear`, `/id`, `/model`, `/tools`, `/cost`, `/sessions` — basics.
- `/yolo` — auto-approve-everything; "always approve this tool" memory at the approval prompt.
- `/plan` — read-only mode; mutations auto-deny. Mutually exclusive with `/yolo`.
- `/skills`, `/memory`, `/todos`, `/agents`, `/mcp`, `/governance` — surface state from each provider.

**Streaming polish**
- Ctrl+C interrupts the in-flight turn (process stays alive).
- Braille spinner while waiting for first content.
- Dim-cyan colour around ``` code fences.

**Context compaction** — `CompactionProvider` keeps long sessions inside the model's input budget. Drops tool results at 50% of context, truncates at 80%.

**Project-context auto-load** — `AgentSkillsProvider` discovers `./skills/<name>/SKILL.md` files at startup, injects each skill's name+description into the system prompt, and auto-registers a `load_skill` tool so the model fetches bodies on demand. Claude Code Skills convention, body-on-demand out of the box.

**Cross-session memory** — `FileMemoryProvider` rooted at `./memory/`. The model autonomously writes and reads notes across sessions via 5 framework-registered `FileMemory_*` tools; the framework maintains a `memories.md` index automatically.

**Structured todo tracking** — `TodoProvider`. The model plans multi-step work as a checklist; the framework auto-registers 5 `TodoList_*` tools and auto-injects the current list into the system prompt each turn. Survives session resume; `/todos` shows ✓/☐ at a glance.

**Fluent middleware pipeline** — `AgentBuilder.cs` uses `AIAgentBuilder.UseLogging().UseOpenTelemetry().UseToolApproval()` instead of an imperative wrap chain. Per-tool-call timing middleware via `FunctionInvocationDelegatingAgentBuilderExtensions.Use(...)`.

**Sub-agent delegation** — `SubAgentsProvider` configures one read-only "researcher" sub-agent. Framework auto-registers 6 `SubAgents_*` tools for the main agent to delegate. The chapter's preview-drift fix story documents two version pins required to land the framework's execution path.

**MCP server integration** — configure `mcpServers` in `agent.json`. At startup the agent connects via the standalone `ModelContextProtocol` 1.3.0 SDK, discovers each server's tools, and adds them to the model's tool list, optionally gated through the existing approval workflow. Canonical example points at Playwright MCP running on localhost.

**Production governance** — `Microsoft.AgentGovernance` (Microsoft-signed, MIT-licensed, public-preview). Opt-in by `./policies/default.yaml`. AGT attaches via a one-line `builder.WithGovernance(adapter)` on the Step 14 builder. Ships YAML-configured policy enforcement, rate limiting, circuit breakers, and tamper-proof audit logging. Covers OWASP Agentic Top 10. `/governance` shows policy state + recent audit events.

## Prerequisites

- .NET 9 SDK
- An Anthropic API key — get one at [console.anthropic.com](https://console.anthropic.com/)

## Run

```bash
export ANTHROPIC_API_KEY=sk-ant-...

dotnet run                          # new session, prints its id
dotnet run -- --continue            # resume the most recent session
dotnet run -- --resume <id-prefix>  # resume a specific one (prefix match, like git/claude)
dotnet run -- --list                # list past sessions, newest first
dotnet run -- --help                # usage
```

Short flags: `-c`, `-r`, `-l`, `-h`.

Commands inside the chat (all slash-prefixed):
- `/help` — list every command
- `/exit` / `/quit` / Ctrl+D — quit (session is saved)
- `/clear` — start a new session in the same process; the previous one stays saved
- `/id` — print the current session id
- `/model` — print the current model
- `/tools` — list registered tools and which require approval
- `/cost` — total token use and cost for this session
- `/sessions` — list past sessions
- `/yolo` — toggle auto-approve-everything mode (off by default)
- `/plan` — toggle plan mode (read-only; mutation tools auto-deny)
- `/skills` — list skills under `./skills/` (workshop convention: `./skills/<name>/SKILL.md`)
- `/memory` — list files written by `FileMemoryProvider` under `./memory/`
- `/todos` — list the model's current todo items (✓ done / ☐ pending) from `TodoProvider`
- `/agents` — list configured sub-agents (Step 15)
- `/mcp` — list configured MCP servers from `agent.json` (Step 16; headers redacted)
- `/governance` — show AGT policy state + recent audit events (Step 17)

At the approval prompt, answers are `y`/`yes` to approve once, `a`/`always` to approve and remember this tool for the rest of the session, anything else to deny.

`/yolo` and `/plan` are mutually exclusive — they're opposite policies. The prompt label changes (`you (plan) > ` or `you (yolo) > `) so the current mode is visible at every turn.

Each session is persisted to `./sessions/<id>.json` after every turn. The id is a short 8-hex-char tag (e.g. `a3f7c102`); prefix-match works as long as it's unambiguous — same as `git checkout abc123` or `claude --resume abc`.

### Example

```text
$ dotnet run
Started new session: a3f7c102
Model: claude-haiku-4-5. Commands: '/exit' (quit), '/clear' (new session), '/id' (show id).

you > what's a monoid?
claude > [...]

you > /exit

(session saved: a3f7c102 — resume with: dotnet run -- --resume a3f7c102)

$ dotnet run -- --list
ID          Updated              Model                   Preview
a3f7c102    2026-05-09 14:32:11  claude-haiku-4-5        what's a monoid?

$ dotnet run -- -r a3f
Resumed session a3f7c102 — what's a monoid?
```

## Tests

```bash
dotnet test                                    # all unit tests
dotnet test --filter Category=Unit             # explicit filter (same set today)
```

**173 unit tests across 29 test classes**, all pure (no API key, no agent invocation). Coverage groups:
- **Persistence** — `SessionStoreTests` (NewId / Enumerate / FindByPrefix).
- **Tools** — one class per tool: `ReadFileTests`, `ListDirTests`, `GlobTests`, `GrepTests`, `WriteFileTests`, `EditFileTests`, `BashTests`. Happy path + edge cases (missing path, oversize, boundary, approval gating).
- **Harness** — `SlashRegistryTests`, `ApprovalPromptTests`, `SpinnerTests`, `MarkdownStreamRendererTests`, `ToolRegistryTests`.
- **Config & observability** — `AgentConfigTests`, `McpConfigTests`, `PricingTests`, `UsageAccumulatorTests`.
- **Provider wiring** — one `<Feature>RegistrationTests` + one `<Feature>CommandTests` per provider: Compaction, Skills, FileMemory, Todo, SubAgents, Mcp, Governance.

Tests that touch the process-global `Console.Out` share `[Collection("Console-shared-static")]` so they don't race each other. Live integration tests (real Claude calls) are gated to a future `Category=Live` tier — see [CLAUDE.md](CLAUDE.md).

## Configure

| Env var | Default | Notes |
|---|---|---|
| `ANTHROPIC_API_KEY` | — | Required |
| `ANTHROPIC_DEPLOYMENT_NAME` | `claude-haiku-4-5` | Try `claude-sonnet-4-6` for harder questions |
| `CLAUDECHAT_LOG_LEVEL` | `Debug` | `Trace` / `Debug` / `Information` / `Warning` / `Error` — controls `claudechat.log` verbosity |

Flags: `--otel` enables the OpenTelemetry console exporter (noisy; off by default). `--config <path>` loads an `agent.json` profile (otherwise `./agent.json` is auto-discovered if present).

Minimal `agent.json`:
```json
{
  "model": "claude-haiku-4-5",
  "instructions": "You are a helpful assistant.",
  "tools": {
    "allow": ["read_file", "list_dir", "glob", "grep"],
    "requireApproval": []
  }
}
```
All fields optional. See [tutorial/06-agent-json.md](tutorial/06-agent-json.md).

## How it works

The framework gives you the round-trip primitives — we wrap them with metadata for the listing UX.

```csharp
// Save: framework gives JsonElement, we add our own metadata around it
var snapshot = await agent.SerializeSessionAsync(session);
var record = new JsonObject {
    ["id"] = id, ["createdAt"] = ..., ["updatedAt"] = ...,
    ["model"] = model, ["preview"] = firstUserMessage,
    ["session"] = JsonNode.Parse(snapshot.GetRawText()),  // the framework blob
};
File.WriteAllText($"sessions/{id}.json", record.ToJsonString());

// Resume: pull the framework blob back out, deserialize
var obj = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
var sessionElem = obj["session"]!.Deserialize<JsonElement>();
AgentSession session = await agent.DeserializeSessionAsync(sessionElem);
```

`AsAIAgent()` is the extension from `Microsoft.Agents.AI.Anthropic` — wraps the Anthropic SDK as an `AIAgent` you can drive with the standard Agent Framework abstractions. `AgentSession` carries conversation state across turns; `Serialize/DeserializeSessionAsync` round-trip it to a `JsonElement` so you can save it anywhere (file, blob, database, Redis). The metadata wrapper (`id`, `createdAt`, `preview`, etc.) is purely so we can implement `--list` and prefix-resume — the framework doesn't track that itself.

## Files

**Project root**
- [claude-code-with-maf.csproj](claude-code-with-maf.csproj) — package pins: `Microsoft.Agents.AI.Anthropic` 1.5.0-preview, `Microsoft.Extensions.AI[.Abstractions]` 10.5.2, `Anthropic` 12.20.1, `ModelContextProtocol` 1.3.0, `Microsoft.AgentGovernance[.Extensions.Microsoft.Agents]` 3.6.0
- [Program.cs](Program.cs) — entry point: arg parsing, `--list`/`--help`, session resolution, `ApprovalState` construction, `BuildResult` unpacking
- [Agent/AgentBuilder.cs](Agent/AgentBuilder.cs) — composes the agent: client → `AsAIAgent` → 7 function tools + 5 `AIContextProvider`s + the fluent middleware pipeline (`UseLogging` / `UseOpenTelemetry` / `UseToolApproval` / MCP-approval + tool-timing `.Use(...)` / `WithGovernance`). Returns `BuildResult(Agent, Todos, Governance, AuditTrail)`.

**Harness** — the "Claude Code feel" layer (not MAF)
- [Harness/ChatLoop.cs](Harness/ChatLoop.cs) — interactive chat loop, slash dispatch, approval round-trip, Ctrl+C handling, Anthropic API error catches
- [Harness/ApprovalPrompt.cs](Harness/ApprovalPrompt.cs) — y/N/a gate for approval-required tools (Step 3; yolo & always from Step 7; plan-mode deny from Step 8)
- [Harness/Commands/SlashDispatch.cs](Harness/Commands/SlashDispatch.cs), [Harness/Commands/ApprovalState.cs](Harness/Commands/ApprovalState.cs) — 16-command registry + shared yolo/plan/always state
- [Harness/Spinner.cs](Harness/Spinner.cs), [Harness/MarkdownStreamRenderer.cs](Harness/MarkdownStreamRenderer.cs) — streaming polish: Braille spinner + fence-aware colour (Step 9)

**Persistence**
- [Persistence/SessionStore.cs](Persistence/SessionStore.cs) — session JSON + metadata wrapper (id/createdAt/preview/model around the framework blob)

**Tools** — all 7 function tools live here
- [Tools/ReadFile.cs](Tools/ReadFile.cs), [Tools/ListDir.cs](Tools/ListDir.cs), [Tools/Glob.cs](Tools/Glob.cs), [Tools/Grep.cs](Tools/Grep.cs) — read-only navigation (Steps 1–2)
- [Tools/WriteFile.cs](Tools/WriteFile.cs), [Tools/EditFile.cs](Tools/EditFile.cs), [Tools/Bash.cs](Tools/Bash.cs) — approval-gated mutations (Step 4)

**Observability & config**
- [Observability/FileLogger.cs](Observability/FileLogger.cs), [Observability/Pricing.cs](Observability/Pricing.cs), [Observability/UsageAccumulator.cs](Observability/UsageAccumulator.cs) — JSON-Lines log, per-model pricing, token accumulator (Step 5)
- [Config/AgentConfig.cs](Config/AgentConfig.cs) — `agent.json` schema + loader (Step 6; `McpServerConfig` added in Step 16)

**Runtime data folders** (configured by their providers, not committed except for `skills/` example)
- [skills/repo-context/SKILL.md](skills/repo-context/SKILL.md) — sample skill auto-discovered by `AgentSkillsProvider` (Step 11)
- `memory/` — `FileMemoryProvider`'s scratchpad; gitignored (Step 12)
- `policies/default.yaml` — opt-in governance policy file; if present, AGT attaches (Step 17)

**Tests**
- [tests/ClaudeChat.Tests/](tests/ClaudeChat.Tests/) — 29 unit-test classes, separate `.csproj`, excluded from main project compile glob

## Notes

The package is prerelease and the MS Learn samples have drifted from the actual API. As of this commit:

| MS Learn sample | Actual API |
|---|---|
| `APIKey` | `ApiKey` |
| `AgentThread` | `AgentSession` |
| `agent.GetNewThread()` | `await agent.CreateSessionAsync()` |
| `WebSearchToolResultContent.Results` | `WebSearchToolResultContent.Outputs` (broke Step 15 sub-agent execution before two csproj pins resolved it: `Microsoft.Extensions.AI[.Abstractions]` @ 10.5.1 unifies the M.E.AI graph; `Anthropic` SDK @ 12.20.1 removes the stale-API references the Microsoft adapter was driving) |

Names verified by reflecting on the restored DLLs. Expect more drift on each prerelease bump.

## What's next

The workshop is complete (17 of 17 steps). Possible follow-ups, none of them required:

- **Track MAF previews.** The package is `1.5.0-preview.*`. Bump it on each new drop, run the build, fix whatever the API renamed this time. The drift table in **Notes** above is the running log.
- **Anthropic-on-Foundry.** Swap `AnthropicClient` for `AnthropicFoundryClient` to route through Azure AI Foundry instead of Anthropic's API directly.
- **More sub-agent profiles.** Step 15 ships one read-only researcher; `SubAgentsProvider` accepts an array. Add a "code-reviewer" or "test-writer" with different toolsets.
- **Default-deny governance.** Step 17 ships with `default_action: allow` so the workshop is friendly out of the box. Flip it to `deny` and write per-tool allow rules to model a real production posture.
- **MCP OAuth & remote servers.** Step 16's example is unauthenticated localhost Playwright. Real MCP deployments use OAuth + remote endpoints — `ModelContextProtocol`'s SDK supports both.
- **Live integration tests.** All 173 tests today are pure (`Category=Unit`). A `Category=Live` tier that hits real Claude, gated to nightly CI, would catch preview drift before it bites a workshop run.
