# 2026-05-15 — The compaction provider serialises in-flight ToolCall references

> Affected chapters: [Step 03 — approval gate](../03-approval-gate.md), [Step 10 — compaction](../10-compaction.md)
> Code changes: `Agent/AgentBuilder.cs` (CompactionProvider detached), `Harness/Commands/SlashDispatch.cs` (new `/compact` command).

## Symptom

The user asked the agent to "create a temp folder, write a hello world C# program in it, make sure it builds." Default model (`claude-haiku-4-5`). The agent immediately fell into a tight loop:

```
I'll create a temp folder with a buildable C# Hello World program for you.
  approve bash(command="mkdir -p /tmp/HelloWorld")? [auto-approved (always)]

Let me create a temp folder with a buildable C# Hello World program.
  approve bash(command="mkdir -p /tmp/HelloWorld")? [auto-approved (always)]

I'll help you create a temp folder with a buildable C# Hello World program.
  approve bash(command="mkdir -p /tmp/CSharpHelloWorld")? [auto-approved (always)]

...
```

Each iteration: model announces the same step, requests approval for a near-identical command, auto-approved, then re-announces the step. No progression.

A second test with `write_file` produced the same pattern — same step, same file, requested over and over. The agent never moved past Step 2 of its own four-step plan.

## Diagnosis

Three wrong hypotheses, then the data.

**Hypothesis 1 — tool output verbosity.** `mkdir -p` succeeds silently. Bash returned `"(no output)"` for that case — rhetorically ambiguous ("did the command actually run?"). Maybe Haiku was reading this as failure and retrying.

Action: change Bash's silent-success return to `"(exit 0; command completed with no output)"`. Built, tested, ran the same prompt. **Same loop.** Hypothesis wrong.

**Hypothesis 2 — system prompt.** The shipped `DefaultInstructions` was a tool-aware *general-purpose assistant* prompt. Maybe Haiku needed an explicit coding-agent prompt — *verify before retrying, don't re-announce the plan, stop and ask when stuck.*

Action: rewrote `DefaultInstructions` in `AgentBuilder.cs` with explicit *coding agent* behaviours: workflow rules, verification habit, no-preamble, when-stuck-stop-and-ask. Ran the same prompt. **Same loop.**

**Hypothesis 3 — framework-level approval persistence.** Right shape, wrong layer. Dumped the failing session's `InMemoryChatHistoryProvider`:

| Content type | Count |
|---|---|
| `assistant/functionCall` (auto-invoked tools) | 3 |
| `tool/functionResult` (auto-invoked tools) | 3 |
| `assistant/toolApprovalRequest` | **7** |
| `user/toolApprovalResponse` | **1** |

Auto-invoked tools were symmetric. Approval-required tools were not — seven requests, only one response, no matching function results. The model literally couldn't see its own previous approvals.

A direct workaround — manually append the approval-response `ChatMessage` to history via `SetInMemoryChatHistory` — produced a different bug: `messages.5.content.1: tool_use ids must be unique` from Anthropic. We were duplicating what the framework *was* doing on the wire, just not in persistent history.

That distinction was the clue: **MAF translates approval responses for the wire (the current API call sees a proper request/result pair), but doesn't persist them to history.** Wire view and history view were out of sync.

**Root cause.** Pointed at by an external doc reference: in MAF preview, serialising the session mid-cycle can lose the polymorphic `ToolCall` type that `ToolApprovalRequestContent` references. The `CompactionProvider` runs on every turn (the log confirmed: `"applying compaction to 5 messages"` on every single `RunStreamingAsync.invoked`, even though 5 messages is nowhere near the 200K-token threshold). Each invocation round-trips the message list through JSON. The concrete `ToolCall` reference gets flattened. MAF's approval middleware can no longer bind the user's response back to the original call — and the model re-emits the request.

The provider's auto-running, on every turn, regardless of whether compaction is actually needed.

## Resolution

Two changes:

1. **Detach `CompactionProvider` from the providers list** (`Agent/AgentBuilder.cs`). The provider's construction stays — it's still part of the Step 10 lesson — but the `AIContextProviders` list no longer includes it:
   ```csharp
   var providers = new List<AIContextProvider>();   // was: { compactionProvider }
   ```
   A dated comment block explains the rationale and the one-token revert path.

2. **Expose compaction as a slash command** (`Harness/Commands/SlashDispatch.cs`). Construct `ContextWindowCompactionStrategy` with the existing workshop constants, wrap as `IChatReducer` via `ChatStrategyExtensions.AsChatReducer()`, call `ReduceAsync` on the session's `InMemoryChatHistory`, write the reduced list back. Sync-over-async because `ISlashCommand.Run` is sync.
   ```
   you > /compact
     compacted: 42 → 17 messages
   ```

The strategy runs *between turns*, when no approval cycle is in flight. There's no live `ToolCall` to corrupt.

## Lesson

**Preview frameworks fail at their seams.** The bug wasn't in any one component. `UseToolApproval` worked. `InMemoryChatHistoryProvider` worked. `CompactionProvider` worked when run alone. The fault lived in *how the three composed when used together* — specifically, that the compaction provider's mid-turn serialisation interacted badly with the approval middleware's expectation of stable `ToolCall` references.

**A practical corollary:** when defensive layers don't behave like defenses, suspect the framework, not the model. Three wrong hypotheses in a row — each one a fix that would have helped if the model were the problem — moved exactly nothing. The fourth hypothesis (the data-driven one) named a layer we didn't initially touch.

The workshop's *one-step-per-sitting* discipline was helpful in reverse here: the three failed fixes were small, isolated, and easy to revert. We weren't stuck in a deep tangle when the real cause finally surfaced.

## Stretch

- **File the upstream issue.** Clean reproduction: fresh session, three consecutive approval-required tool calls, `InMemoryChatHistoryProvider.Messages` shows asymmetric counts. Likely fixable in MAF by ensuring the compaction round-trip preserves `ToolCall` polymorphic types — or by skipping compaction when any message contains a live `ToolApprovalRequestContent`.
- **Verify whether the strategy-only path is safe long-term.** The current `/compact` command tests the hypothesis that *between-turn* invocation avoids the serialisation bug. If it also bites, the strategy's own implementation contains the broken path — and the workaround becomes simpler (drop oldest N messages, no strategy at all).
- **Re-enable the provider after the upstream fix.** One-token change in `AgentBuilder.cs`: re-add `compactionProvider` to the providers list, remove the dated comment.
