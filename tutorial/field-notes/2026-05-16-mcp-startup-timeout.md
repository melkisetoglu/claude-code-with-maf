# 2026-05-16 — MCP server discovery could hang Build() indefinitely; bounded with a 10s startup CTS

> Affected chapters: [Step 16 — MCP](../16-mcp.md)
> Code changes: `Agent/AgentBuilder.cs`.

## Symptom

`AppendMcpServerToolsAsync` was called with `CancellationToken.None`. If `agent.json` listed an MCP server that accepted connections but never responded (stale localhost port, slow handshake, wrong process bound to the port), startup hung silently. The Ctrl+C handler isn't installed until `ChatLoop` starts, so the user's only recovery was killing the terminal. `ConnectAndListToolsAsync` made it worse by catching every `Exception` (including cancellation) and rewrapping it as a misleading "is the server running?" `InvalidOperationException`.

## Resolution

Two edits.

1. `ConnectAndListToolsAsync` now has a two-arm catch: `OperationCanceledException` propagates unchanged, `Exception` keeps the existing connection-failure rewrap.
2. `Build()` wraps the discovery call in a 10s `CancellationTokenSource` and converts only its own timeout — `when (mcpStartupCts.IsCancellationRequested)` — to an `InvalidOperationException` that names the cause: *"MCP server discovery timed out after 10s. Check agent.json mcpServers — is the server actually running?"*

No new test; the natural one (localhost listener that accepts but never responds) is heavyweight for a well-understood failure mode.
