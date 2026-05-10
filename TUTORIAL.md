# Build a Claude Code-style console agent with Microsoft Agent Framework

A workshop that grows a console AI agent from "hello world" to something close to Claude Code, one step at a time. Built on **Microsoft Agent Framework (MAF)** in .NET 9, talking to **Claude** via the official Anthropic SDK adapter.

## Who this is for

You've written .NET before, and you've used an LLM API before, but you haven't built an agent. You want to learn MAF properly — what it gives you, what you have to build, where the seams are — by building something real.

## What we're building

A terminal agent with: streaming chat, named persistent sessions, file/shell tools, tool-approval prompts, slash commands, plan mode, context compaction, project memory, sub-agents, and MCP integration. Roughly: **Claude Code, but yours, in C#, on Claude.**

We get there in 17 small steps across 6 milestones. Each step is one sitting.

## How to follow along

Each step has a chapter in [`tutorial/`](tutorial/) with:
- **Goal** — what you'll have working at the end
- **MAF concept** — the framework idea this step teaches
- **Code walkthrough** — the actual edits, with reasoning
- **Verify** — how to know it works
- **Pitfalls** — things that bit me / will probably bite you
- **Stretch** — optional exercises if you want to go further

The single project at the repo root grows with each step. Run [`dotnet run`](README.md) at any time to use what you've got.

> **Tip — use git tags per step.** Each step ends in a clean state we can tag (`step-00`, `step-01`, …). That way you can `git checkout step-03` to see the project as it was at the end of Step 03, or diff between any two steps to see exactly what changed. Commit messages use a `[step-NN]` prefix so `git log --oneline` reads like a table of contents.

## Honest framing — read me first

- **MAF is preview.** The package is `Microsoft.Agents.AI.Anthropic 1.5.0-preview.*`. APIs rename between versions, the published docs lag, and some features are in the assembly with no public sample. We've already caught three doc/code drifts in Step 0. **Expectation:** when something doesn't compile, reflect on the restored DLLs to find the real names. There's a probe pattern in Step 0's pitfalls section.
- **MAF is the runtime, not the UX.** Slash commands, status lines, plan mode, the diff-preview-before-edit dialog — all of that is *harness* code that you write. MAF gives you the agent loop, tools, sessions, compaction, sub-agents. Don't expect Claude Code's UX to fall out of MAF for free; we build it.
- **This tutorial is in motion, not arrived.** I'm writing chapters as I do the steps. Steps marked _planned_ below are scaffolded but not yet written.

---

## The plan

### Milestone 0 — Foundation
| # | Step | Status |
|---|---|---|
| 00 | [Streaming chat + named persistent sessions](tutorial/00-baseline.md) | ✅ done |

### Milestone 1 — The agentic loop (tools + safety)
*The single biggest leap. Without tools, this is chat. With tools, it's an agent.*

| # | Step | Status |
|---|---|---|
| 01 | [First read-only tool: `read_file`](tutorial/01-read-file.md) | ✅ done |
| 02 | File-system toolset: `list_dir`, `glob`, `grep` | _planned_ |
| 03 | Tool-approval gate (`ToolApprovalAgent`) | _planned_ |
| 04 | Mutation tools: `write_file`, `edit_file`, `bash` (gated by step 3) | _planned_ |

### Milestone 2 — Observability
*Cheap, do it before the harness gets complex. Debugging without it is misery.*

| # | Step | Status |
|---|---|---|
| 05 | Logging + OpenTelemetry + per-turn token/cost | _planned_ |

### Milestone 3 — Configuration
| # | Step | Status |
|---|---|---|
| 06 | External `agent.json`: model, system prompt, tool allowlist, approval rules | _planned_ |

### Milestone 4 — Harness UX (the Claude Code feel)
| # | Step | Status |
|---|---|---|
| 07 | Slash commands (`/help`, `/clear`, `/tools`, `/cost`, `/model`, `/sessions`) | _planned_ |
| 08 | Plan mode (read-only tool subset) | _planned_ |
| 09 | Streaming polish: Ctrl+C interrupt, spinner, syntax highlighting | _planned_ |
| 10 | Context compaction (`CompactionProvider`) | _planned_ |

### Milestone 5 — Memory & workflow
| # | Step | Status |
|---|---|---|
| 11 | Project-context auto-load (CLAUDE.md style) via `AgentSkillsProvider` | _planned_ |
| 12 | Cross-session memory (`FileMemoryProvider`) | _planned_ |
| 13 | Todo tracking (`TodoProvider`) | _planned_ |

### Milestone 6 — Power features
| # | Step | Status |
|---|---|---|
| 14 | Hooks / middleware (delegating agent pattern) | _planned_ |
| 15 | Sub-agents (`SubAgentsProvider`) | _planned_ |
| 16 | MCP server integration | _planned_ |
| 17 | Budgets & circuit breakers | _planned_ |

---

## The MAF mental model — one paragraph

There's an `AIAgent`. You feed it user input plus an `AgentSession` (the conversation state). You get back text and content updates. You wrap it with **delegating agents** — `ToolApprovalAgent`, `LoggingAgent`, `OpenTelemetryAgent` — to add cross-cutting behavior without changing the core. You attach **providers** — `CompactionProvider`, `SubAgentsProvider`, `FileMemoryProvider`, `AgentSkillsProvider` — to extend its capabilities. **Tools** are functions you register that the agent can call. The session can be **serialized to JSON** so conversations survive restarts.

That's it. The rest of this tutorial is layering those pieces, one at a time, and noticing what's still missing for a real Claude-Code-class experience.

## Prerequisites

- .NET 9 SDK
- An Anthropic API key
- A terminal you like

## Where to start

→ **[Step 00 — Baseline: streaming chat + sessions](tutorial/00-baseline.md)**
