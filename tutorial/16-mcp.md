# Step 16 — MCP server integration via the standalone SDK

> *Goal: connect the agent to a real Model Context Protocol server (Playwright MCP) over HTTP, discover the server's tools at startup, and let the model call them through a workshop-specific approval gate. End-to-end browser automation via 26 framework-supplied browser tools, opt-in by config.*

The Model Context Protocol is Anthropic's open standard for exposing tools to LLMs. **Claude Desktop, Claude Code, Cursor, VS Code Copilot, Kiro, Codex, etc.** all integrate MCP. Step 16 wires our workshop agent into that ecosystem.

This step is the workshop's **most-pivoted single step.** Three discoveries shape its design:

1. MAF's native `HostedMcpServerTool` abstraction exists but isn't translated by the preview Anthropic adapter — the tool never reaches the model.
2. The standalone `ModelContextProtocol` 1.3.0 SDK works — discovery + invocation flow end-to-end.
3. **Wrapping `McpClientTool` with `ApprovalRequiredAIFunction` (the workshop's Step 3 approval gate) silently breaks invocation** — after approval the framework doesn't actually call the tool. Model loops.

Each discovery has its own pitfall section below. The shipping wiring uses the SDK for transport AND a **separate function-invocation middleware** (Step 14 pattern) for approval — bypassing both broken paths.

## What you'll have at the end

Three terminals:

```text
# Terminal 1 — Playwright MCP server (one-time setup, runs as long as you use it)
$ npx @playwright/mcp@latest --port 8931
Listening on http://localhost:8931
```

```text
# Terminal 2 — your editor
$ cat agent.json
{
  "mcpServers": [
    {
      "name": "playwright",
      "address": "http://localhost:8931/mcp",
      "description": "Local browser automation via Playwright MCP",
      "approvalMode": "always"
    }
  ]
}
```

```text
# Terminal 3 — the workshop agent
$ dotnet run
you > /mcp
Configured MCP servers (1):
  playwright
    address      : http://localhost:8931/mcp
    description  : Local browser automation via Playwright MCP
    approvalMode : always

you > Open https://example.com and screenshot the page.
claude > I'll navigate to https://example.com and take a screenshot.
  approve browser_navigate(url="https://example.com")? [y/N/a=always]:  y
[browser_navigate: url="https://example.com"]
  approve browser_take_screenshot(...)? [y/N/a=always]:  y
[browser_take_screenshot: ...]
Done — screenshot saved as page-2026-05-12.png. The page shows: ...
```

The model sees 26 Playwright browser tools (`browser_navigate`, `browser_click`, `browser_fill_form`, `browser_take_screenshot`, etc.) alongside our workshop's 7 tools, the framework's auto-registered tools (load_skill, FileMemory_*, TodoList_*, SubAgents_*) — total tool surface grows to ~50 tools, all addressable through the same `[tool: …]` rendering, all gated through the same approval prompt when `approvalMode = "always"`.

If `agent.json` doesn't have `mcpServers`, the workshop behaves exactly as Step 15 did. Opt-in by config.

## MAF concepts introduced — and the pivot

### 1. `HostedMcpServerTool` (Microsoft.Extensions.AI) — what MAF *designed*

MAF ships an MCP abstraction in `Microsoft.Extensions.AI.Abstractions`:

```csharp
new HostedMcpServerTool(serverName, serverAddress) {
    ApprovalMode = new HostedMcpServerToolAlwaysRequireApprovalMode(),
    AllowedTools = [...],
    Headers      = {...},
};
```

Plus four approval modes: `Always`, `Never`, `RequireSpecific(always, never)`, and an open base for custom logic. This is the "hosted MCP" pattern — Anthropic's API receives the server URL, Anthropic's infrastructure makes the HTTP call to the MCP server, the tools flow back to the model as if they were native API tools.

**The good:** clean abstraction, first-class approval gating, no SDK dependency.

**The catch:** Anthropic's API needs to *reach* the URL. For `http://localhost:8931/mcp`, that means Anthropic's servers can't get there. Hosted MCP works for public URLs (or via tunneling).

**The actual catch in this preview:** even with a public URL, `Microsoft.Agents.AI.Anthropic 1.5.0-preview.260507.1` doesn't appear to translate `HostedMcpServerTool` into Anthropic's API's `mcp_servers` parameter. Verified live: I attached a `HostedMcpServerTool`, ran the model with `--list every tool`, the model listed 24 tools (everything except the MCP one), token count was within 50 of baseline — meaning the tool definition was never sent to the API. No error, no warning. Silent drop.

That's where we stopped trying to use the MAF abstraction and pivoted.

### 2. `ModelContextProtocol` 1.3.0 SDK — what *works* today

The standalone SDK is the official .NET MCP client/server library, maintained alongside the protocol itself. Three pieces matter:

| Type | Purpose |
|---|---|
| `HttpClientTransport(options, loggerFactory)` | HTTP/SSE transport for connecting to an MCP server URL. |
| `HttpClientTransportOptions` | `{ Endpoint, Name, AdditionalHeaders, OAuth, ... }` |
| `McpClient.CreateAsync(transport, options, loggerFactory, ct)` | Static async factory returning a live `McpClient`. |

Once you have an `McpClient`, the key method is:

```csharp
IList<McpClientTool> tools = await client.ListToolsAsync();
```

And the headline finding: **`McpClientTool` extends `Microsoft.Extensions.AI.AIFunction` directly.** Once you've listed tools, they ARE `AIFunction` instances — no wrapping, no adapter glue. You add them straight to your agent's `tools` list.

### 3. Approval gating via a separate function-invocation middleware

We **don't** wrap MCP tools with `ApprovalRequiredAIFunction` — that path silently breaks invocation (see the "Second pivot" pitfall below). Instead, MCP approval lives in a dedicated function-invocation middleware (`McpApprovalMiddleware`) that sits at the same seam as Step 14's tool-timing middleware.

UX matches the workshop's existing gate (`[y/N/a=always]` prompt, `PlanMode > YoloMode > AlwaysApprove > prompt` precedence, shared `ApprovalState`). The mechanism differs — but the user can't tell.

## Code walkthrough

### `agent.json` schema extension — `Config/AgentConfig.cs`

```csharp
public sealed record AgentConfig(
    string? Model,
    string? Instructions,
    ToolsConfig? Tools,
    IReadOnlyList<McpServerConfig>? McpServers);

public sealed record McpServerConfig(
    string Name,
    string Address,
    string? Description,
    string? ApprovalMode,
    IReadOnlyList<string>? AllowedTools,
    IReadOnlyDictionary<string, string>? Headers);
```

| Field | Required | Meaning |
|---|---|---|
| `name` | yes | Friendly identifier shown in `/mcp` output and used in fail-fast error messages. |
| `address` | yes | Full URL (e.g., `http://localhost:8931/mcp`). |
| `description` | no | Free-text annotation for the user via `/mcp`; not sent to the model. |
| `approvalMode` | no | `"always"` (default) or `"never"`. Controls `ApprovalRequiredAIFunction` wrapping. |
| `allowedTools` | no | Allowlist of server-side tool names. Tools NOT in the list are silently filtered. |
| `headers` | no | Dictionary of HTTP headers (e.g., bearer auth). Values are redacted in `/mcp` output. |

### Wiring — `Agent/AgentBuilder.cs`

```csharp
// Step 16: connect to any configured MCP servers and append their tools.
AppendMcpServerToolsAsync(tools, config?.McpServers, loggerFactory, CancellationToken.None)
    .GetAwaiter().GetResult();
```

Sync-over-async at startup (same trade-off Step 13 made for `/todos`). The async method:

```csharp
public static async Task AppendMcpServerToolsAsync(...)
{
    foreach (var s in servers)
    {
        var (clientTools, _) = await ConnectAndListToolsAsync(s, loggerFactory, ct);

        var requireApproval = NormalisedMcpApprovalMode(s) == "always";

        // Optional allowedTools filter
        IEnumerable<McpClientTool> filtered = clientTools;
        if (s.AllowedTools is { Count: > 0 })
            filtered = clientTools.Where(t => allow.Contains(t.Name));

        foreach (var t in filtered)
            tools.Add(requireApproval
                ? new ApprovalRequiredAIFunction(t)
                : (AITool)t);
    }
}
```

`ConnectAndListToolsAsync` builds the transport, creates the client, calls `ListToolsAsync`. On any exception it rethrows `InvalidOperationException` with a message that names the server, the address, the underlying error, AND the exact npx command to start a Playwright MCP server. **Fail-fast at startup — silent degrade was tempting but would leave users wondering why the model can't see their tools.**

### Slash command — `Harness/Commands/SlashDispatch.cs`

```csharp
internal sealed class McpCommand : ISlashCommand
{
    public string Name => "/mcp";
    public string Description => "List configured MCP servers (Step 16; from agent.json mcpServers)";

    public SlashAction Run(SlashContext ctx)
    {
        var servers = ctx.Config?.McpServers;
        if (servers is null || servers.Count == 0) { /* opt-in hint */ }
        // ... list name, address, description, approvalMode, allowedTools, headers (redacted) ...
    }
}
```

Reads from config, prints. `/tools` stays workshop-tools-only — the dedicated `/mcp` command matches the workshop's "one slash command per feature" pattern (`/skills`, `/memory`, `/todos`, `/agents`).

### Package reference — `claude-code-with-maf.csproj`

```xml
<PackageReference Include="ModelContextProtocol" Version="1.3.0" />
```

Pulled `Microsoft.Extensions.AI.Abstractions` up from 10.5.1 (Step 15's pin) to 10.5.2 (the SDK's minimum). Both pins documented in the csproj comment.

## Verify

Three flows to check.

### Flow 1 — default behavior (no MCP servers)

```bash
dotnet build && dotnet test --filter Category=Unit    # 169 tests pass
```

```text
$ dotnet run               # without agent.json
you > /mcp
(no MCP servers configured — add an `mcpServers` entry to agent.json to opt in.)
see tutorial/16-mcp.md for an example pointing at Playwright MCP.
```

### Flow 2 — fail-fast when configured but unreachable

```text
$ cat agent.json
{ "mcpServers": [{ "name": "playwright", "address": "http://localhost:8931/mcp" }] }

$ dotnet run               # Playwright MCP NOT running
agent.json config error: failed to connect to MCP server 'playwright' at
http://localhost:8931/mcp: HttpRequestException: Connection refused (localhost:8931).
is the server running? (e.g., `npx @playwright/mcp@latest --port 8931`)
```

The error names the server, the address, the underlying problem, and the exact fix command.

### Flow 3 — end-to-end with Playwright MCP

```bash
# Terminal A
$ npx @playwright/mcp@latest --port 8931
Listening on http://localhost:8931
```

```text
# Terminal B
$ dotnet run
you > List every tool name available to you.
claude > read_file, list_dir, glob, grep, write_file, edit_file, bash,
         browser_close, browser_resize, browser_navigate, browser_click,
         browser_take_screenshot, browser_snapshot, browser_fill_form,
         browser_evaluate, ... (26 browser_* tools total), load_skill,
         FileMemory_*, TodoList_*, SubAgents_*

you > Open https://example.com and screenshot it.
claude > [browser_navigate: url="https://example.com"]
  approve browser_navigate(url="https://example.com")? [y/N/a=always]: y
[browser_take_screenshot: filename="example-com.png"]
  approve browser_take_screenshot(...)? [y/N/a=always]: y
Screenshot saved. The page shows...
```

Token count jumps ~4,200 from baseline — that's 26 browser tool schemas averaging ~160 tokens each. Tool-timing middleware from Step 14 fires on each call (`→ 234ms` etc.). Approval gate from Step 3 prompts before each call.

## Pitfalls

### Second pivot — `ApprovalRequiredAIFunction(McpClientTool)` silently breaks invocation

The *original* approach for Step 16 had the SDK working but wrapped each `McpClientTool` with `ApprovalRequiredAIFunction` so the framework's standard approval gate (Step 3 / `ToolApprovalAgent`) would route MCP calls through `[y/N/a=always]` prompts. Same code path as `write_file` / `bash`.

**It broke.** Symptom: the model emits a tool call, the user (or `a`/`always` memory) auto-approves, the user sees:

```
I'll open https://example.com and take a screenshot for you.
  approve browser_navigate(url="https://example.com")? [auto-approved (always)]

I'll open https://example.com and take a screenshot for you.
  approve browser_navigate(url="https://example.com")? [auto-approved (always)]
```

…repeating forever within a single turn. No `[browser_navigate: ...]` execution line, no `→ Nms` timing from Step 14, just the same approval cycle.

**Diagnosis:** the `claudechat.log` was the smoking gun. The MCP client was alive (steady `ping` keepalives every 3 seconds), but **no `tools/call` JSON-RPC requests were ever sent**. The framework's approval flow:

1. Model emits tool call
2. `ApprovalRequiredAIFunction` intercepts → emits `ToolApprovalRequestContent`
3. ChatLoop auto-approves → sends `ToolApprovalResponseContent` back
4. Framework receives the approval response **but doesn't invoke `McpClientTool`**
5. Model receives nothing useful → tries the same call again

The framework's post-approval invocation path appears to be optimised for `AIFunctionFactory.Create(method)`-produced functions — `McpClientTool` is a custom `AIFunction` subclass with its own `InvokeCoreAsync`, and the approval handler doesn't reach it. (`load_skill`, `FileMemory_*`, etc. don't go through `ApprovalRequiredAIFunction`, so they're unaffected — that's why earlier provider-auto-tools have always worked.)

**Fix:** Don't wrap MCP tools with `ApprovalRequiredAIFunction` at all. Instead, gate them via **function-invocation middleware** at the same seam as Step 14's tool-timing middleware. The middleware:

```csharp
private static async ValueTask<object?> McpApprovalMiddleware(
    FunctionInvocationContext fnCtx,
    Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
    CancellationToken ct,
    ApprovalState approvalState,
    HashSet<string> approvalRequiredNames)
{
    if (!approvalRequiredNames.Contains(fnCtx.Function.Name))
        return await next(fnCtx, ct);

    if (approvalState.PlanMode)        return "[denied: in plan mode]";
    if (approvalState.YoloMode)        return await next(fnCtx, ct);
    if (approvalState.AlwaysApprove.Contains(fnCtx.Function.Name))
                                       return await next(fnCtx, ct);

    Console.Write($"  approve {fnCtx.Function.Name}? [y/N/a=always]: ");
    // ...prompt, then call next() or return denial string...
}
```

Because we control `next(fnCtx, ct)` directly, the approval path **invokes** `McpClientTool`. No framework-layer indirection to fail through. The UX matches the workshop's existing gate (`PlanMode > YoloMode > AlwaysApprove > prompt`), but routed through a different mechanism.

**Wiring impact:** `ApprovalState` now lives in `Program.cs` (was in `ChatLoop`) so `AgentBuilder` can capture it for the middleware. `AgentBuilder.Build()` gained an `ApprovalState` parameter. `ChatLoop.RunAsync` receives it as a parameter instead of constructing one.

**Verified live:** with the middleware, `browser_navigate` produces `→ 5051ms` (a real Playwright navigation), screenshot fires, model summarises the result. No loop.

**Generalised workshop rule:**

> When the framework's approval flow appears to swallow a tool call, look at whether the wrapped function is a custom `AIFunction` subclass (vs an `AIFunctionFactory.Create`-produced one). If it is, bypass `ApprovalRequiredAIFunction` and gate at the function-invocation middleware seam instead. The middleware path lets you control invocation explicitly — no implicit "framework will figure out how to call this" assumption.

### The pivot — `HostedMcpServerTool` doesn't reach the model in this preview

Reading order if you want the diagnosis story:

1. We built the agent.json schema + wired `HostedMcpServerTool` with all four approval modes.
2. Ran the model with `--list every tool` against a configured-but-unreachable URL.
3. Token count: 4038 in. Same as without MCP configured.
4. Started Playwright MCP locally; reran.
5. Token count: 4038 in. **Same as without MCP configured.**
6. Asked the model directly: "do you have any browser tools?". Answer: "No, I don't."

`HostedMcpServerTool` construction succeeds, `/tools` (in our first iteration) showed it, but the Anthropic preview adapter doesn't translate the type into the `mcp_servers` API parameter. Silent drop. No error in `claudechat.log`, no warning, no runtime exception.

The diagnosis pattern is the same as Step 15's: live probe → token-count check → ask the model what it actually sees → don't assume the abstraction works just because it compiles. Trust your eyes, not the type system.

The chapter ships the SDK-based wiring because that's what actually delivers tools to the model. When the adapter catches up, a future step could migrate to `HostedMcpServerTool` (smaller code, no SDK package) — at that point the SDK approach becomes the stretch.

### Don't ship a hardcoded `agent.json` with MCP servers in the workshop

Workshop's default `dotnet run` should work for anyone who clones the repo, regardless of whether they have Node installed. If `agent.json` shipped pointing at `http://localhost:8931/mcp`, every clone would fail-fast at startup with the "is the server running?" error.

Our shipping default: **no `agent.json`** in the repo at all. The chapter has the example; users opt in by creating their own.

### Fail-fast vs silent-degrade

We chose fail-fast: if you put a server in `agent.json` and it's not reachable, startup errors out. The alternative — silently dropping the server, logging a warning, continuing — would have the workshop launching cleanly but the model unable to see the tools the user thought were wired. That's worse: the user spends time wondering why the model "doesn't understand" instead of being told upfront the server isn't running.

Trade-off: opening Playwright MCP and forgetting it crashes `dotnet run`. The error message is informative, so this is fine.

### Sync-over-async at startup

`Build()` is sync; the MCP SDK is async. We call `.GetAwaiter().GetResult()` once at startup — same pattern as `/todos` in Step 13. Cost: per-server HTTP round-trip blocks the main thread before the agent starts. For one-or-two servers this is invisible (~50ms). For many servers, an `await`-friendly `BuildAsync` would be cleaner. Stretch.

### Approval gating composes with the existing workshop tools — no new gate code

Because `McpClientTool` extends `AIFunction`, wrapping with `ApprovalRequiredAIFunction` is mechanical. The user sees the same `[y/N/a=always]` prompt for `browser_navigate` they see for `write_file`. `/yolo` toggles approval-off for both. `a`/`always` per-tool memory works. That composition is genuinely clean — the SDK chose a shape that played well with M.E.AI conventions.

If you ever want a per-tool approval mode (some Playwright tools always-require, others never-require), wrap each with `ApprovalRequiredAIFunction` conditionally based on tool name. Stretch.

### `allowedTools` filters silently

If you set `allowedTools: ["browser_navigate"]` and the server doesn't have a tool by that name, the filter silently produces zero exposed tools. No warning. Verify by `/mcp` (the allowedTools list appears) AND a model "list every tool" check.

### Two version-pin precedents now in csproj

- Step 15 pinned `Microsoft.Extensions.AI[.Abstractions]` at 10.5.1.
- Step 16 bumped both to 10.5.2 (the SDK's minimum).
- `Anthropic` SDK pin from Step 15 stays at 12.20.1.

Three direct pins in csproj now. The diagnosis trail (multi-version splits, MissingMethodException, etc.) is documented in the csproj comment, which is now a small but load-bearing piece of workshop infrastructure for future MAF preview drift.

### `McpClient` not disposed

The current implementation discards the `McpClient` reference after `ListToolsAsync` returns — the underlying `HttpClientTransport` stays alive (the framework holds it via the registered tools), but we don't have a clean shutdown path. For long-running sessions this is fine; the process exit closes connections. For production agents that resume sessions, tracking clients in `BuildResult` for explicit disposal is a stretch.

## Stretch

- **Per-tool approval mode.** Wrap each `McpClientTool` selectively based on tool name.
- **Multiple MCP servers.** Workshop supports it (the foreach is unchanged); chapter exercise wires Playwright + filesystem MCP + time MCP simultaneously.
- **stdio transport.** Use `StdioClientTransport` instead of `HttpClientTransport` to spawn an MCP server subprocess (the way Claude Desktop / Claude Code work). Lets you skip the separate-terminal step.
- **`BuildAsync` for clean async startup.** Remove the sync-over-async hack. Ripples through `Program.cs`.
- **Migrate to `HostedMcpServerTool` when the adapter catches up.** When `Microsoft.Agents.AI.Anthropic` ships full MCP translation, the workshop chapter becomes "you used to wire MCP via the SDK; here's the one-line replacement using the native abstraction." Probe-first methodology will surface the transition.
- **`/mcp status`** — show per-server connection state, last-call time, error history. Requires tracking clients in `BuildResult`.
- **Auth via OAuth.** `HttpClientTransportOptions` has an `OAuth` slot; agent.json could carry an OAuth config. Workshop demonstrates static bearer tokens via `headers`; OAuth is real-world.

## Where the seams are

- **MCP integration ships at the AITool layer, not the provider layer.** Unlike Steps 11–15 where each `AIContextProvider` added a slot to `options.AIContextProviders`, MCP tools go into the same `tools` list as `read_file` and `bash`. From the framework's perspective they're indistinguishable. That's intentional — MCP is a tool-set extension, not a provider extension.
- **Approval composition is the bright spot of the SDK path.** Because `McpClientTool` is an `AIFunction`, every Step 3/7 mechanism (approval prompt, `/yolo`, `a`/`always` memory) works without modification. The pivot from `HostedMcpServerTool` (with its own approval mode taxonomy) to the SDK approach unified the gate.
- **The pivot is the lesson.** Steps 11–14 taught "probe before writing" (you might find the framework already shipped it). Step 15 taught "diagnose preview drift via dep-graph + adjacent SDK upgrades." Step 16 teaches the harder variant: **sometimes the framework's abstraction is incomplete in this preview, and the right move is to bypass it via the underlying SDK.** The workshop-level rule generalises:
  1. Try the framework's abstraction first (probe lives at this layer).
  2. Verify the abstraction *reaches the model* — token-count or "list every tool" check.
  3. If it doesn't, drop one layer down to the SDK / protocol that the abstraction wraps.
  4. Document the pivot honestly and ship what works today.

## Next

→ **Step 17 — Budgets & circuit breakers** *(planned)*: cap token spend per session, fail closed at thresholds, surface usage in `/cost`. Last step of the workshop. The probe-first rule applies — reflect on what MAF ships for cost/budget enforcement (a `BudgetProvider`? middleware?) before designing the harness.
