# 2026-05-15 — The compaction provider serialises in-flight ToolCall references

> Affected chapters: [Step 03 — approval gate](../03-approval-gate.md), [Step 10 — compaction](../10-compaction.md)
> Code changes: `Agent/AgentBuilder.cs`, `Harness/Commands/SlashDispatch.cs`.

## Symptom

The agent fell into a tight loop on approval-required tool calls. Asked to "create a temp folder, write a hello world C# program, make sure it builds," it announced the same step, requested approval for a near-identical `bash` command, got auto-approved, and re-announced the step — over and over, no progression. `write_file` produced the same pattern.

The failing session's `InMemoryChatHistoryProvider` showed asymmetric counts: 7 `assistant/toolApprovalRequest` messages vs 1 `user/toolApprovalResponse` — the model literally couldn't see its own previous approvals. Manually appending the response message produced `tool_use ids must be unique` from Anthropic, ruling out a missing-history fix.

Root cause: `CompactionProvider` ran on every turn (the log confirmed `"applying compaction to 5 messages"` even when nowhere near the 200K-token threshold). Each invocation round-tripped the message list through JSON. In MAF preview, that round-trip flattens the polymorphic `ToolCall` reference inside `ToolApprovalRequestContent`, so the approval middleware can no longer bind the user's response back to the original call — and the model re-emits the request.

## Resolution

Two edits.

1. **Detach `CompactionProvider` from the providers list** (`Agent/AgentBuilder.cs`). The construction stays — it's still part of the Step 10 lesson — but the `AIContextProviders` list no longer includes it. A dated comment block explains the rationale and the one-token revert path.

2. **Expose compaction as a `/compact` slash command** (`Harness/Commands/SlashDispatch.cs`). Construct `ContextWindowCompactionStrategy` with the workshop constants, wrap as `IChatReducer` via `ChatStrategyExtensions.AsChatReducer()`, call `ReduceAsync` on the session's `InMemoryChatHistory`, write the reduced list back. Runs *between turns*, when no approval cycle is in flight — no live `ToolCall` to corrupt.

Output:
```
you > /compact
  compacted: 42 → 17 messages
```
