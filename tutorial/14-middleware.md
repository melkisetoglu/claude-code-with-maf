# Step 14 — Hooks / middleware via the `AIAgentBuilder` pipeline

> *Goal: the wrap chain in `AgentBuilder.cs` stops being imperative reassignment lines and becomes a fluent builder. We add one new piece of cross-cutting behavior — tool-call timing — using the framework's function-invocation middleware seam, the one we haven't shown yet.*

Steps 3 and 5 wired three delegating agents the imperative way:

```csharp
inner = new LoggingAgent(inner, logger);
if (enableOtel) inner = new OpenTelemetryAgent(inner, source);
return new ToolApprovalAgent(inner, opts);
```

That works. It also reads exactly like what it is: hand-rolled chain reassignment. **MAF has shipped a proper fluent middleware pipeline the whole time** — `AIAgentBuilder` with `.Use(...)` and a family of `.UseLogging` / `.UseOpenTelemetry` / `.UseToolApproval` extension methods. Step 14 switches to it, and uses the upgrade as the entry point for one new piece of original middleware: per-tool-call timing.

This is the **fourth retroactive discovery** in the workshop. Step 11 hit it with `AgentSkillsProvider`'s auto-registered `load_skill`. Step 12 hit it with `FileMemoryProvider`'s 5 auto-tools and (later) `FileAccessProvider`'s parallel set. Step 13's `TodoProvider` continued the pattern. Step 14's lesson: **the framework's *extension shape* itself was also hand-rolled.** We've been writing the imperative form of what `AIAgentBuilder` exists to do.

The workshop-level rule from Steps 11–13 ("probe the model's tool list before writing tools that overlap with a framework feature") generalizes for Step 14: **probe the framework's builder + extension surface before writing the wrap chain by hand.**

## What you'll have at the end

```text
you > list the .md files at the repo root, then summarize TUTORIAL.md.
claude > [glob: pattern="*.md", root="."]

         [read_file: path="TUTORIAL.md"]
           → 25ms                        ← glob completed
           → 1ms                         ← read_file completed
         Here are the .md files and the summary...
```

Every tool call — workshop-original (`read_file`, `bash`) AND framework-auto-registered (`load_skill`, `FileMemory_*`, `TodoList_*`) — gets its elapsed time printed on the line below the bracket announcement. Visible per-call latency, costs ~0 tokens, opens the door for "is the model stuck on something slow?" diagnostics.

The wrap chain in `AgentBuilder.cs` also reads better:

```csharp
var builder = new AIAgentBuilder(inner).UseLogging(loggerFactory, _ => { });
if (enableOtel) builder = builder.UseOpenTelemetry(OtelSourceName, _ => { });
builder = builder.UseToolApproval(JsonSerializerOptions.Default).Use(ToolTimingMiddleware);
var wrapped = builder.Build(serviceProvider);
```

## MAF concepts introduced

### 1. `AIAgentBuilder` — the framework's middleware pipeline

```csharp
new AIAgentBuilder(innerAgent)
    .Use(...)
    .Use(...)
    .Build(serviceProvider);
```

The builder holds the inner agent and an ordered list of "wrap" functions. `.Build(sp)` walks the chain inside-out and returns the outermost wrapped `AIAgent`. The DI argument is for `.Use` overloads that want services; if none of yours do, a one-line empty container works:

```csharp
var serviceProvider = new ServiceCollection().BuildServiceProvider();
```

### 2. The four `.Use(...)` overloads

| Overload | When |
|---|---|
| `Use(Func<AIAgent, AIAgent>)` | Plain wrap — receive the inner, return the outer. Simplest. |
| `Use(Func<AIAgent, IServiceProvider, AIAgent>)` | Wrap that needs services from the DI container. |
| `Use(Func<messages, session, options, innerInvoke, ct, Task>)` | Non-streaming middleware function — pre/post around `RunAsync`. |
| `Use(runFn, streamFn)` | Both call paths intercepted with a function pair. |

The four shapes cover the same ground at different abstraction levels — pick the one whose granularity matches what you're doing. We don't use any of these directly in Step 14; we use the higher-level *function-invocation* middleware next.

### 3. The shipped `.UseX` extension methods

| Extension | What it does |
|---|---|
| `UseLogging(loggerFactory, configure)` | Wraps with `LoggingAgent` (Step 5). The `configure` callback lets you tweak the logger after construction. |
| `UseOpenTelemetry(sourceName, configure)` | Wraps with `OpenTelemetryAgent` (Step 5). |
| `UseToolApproval(jsonOptions)` | Wraps with `ToolApprovalAgent` (Step 3). |

We hand-rolled the constructor calls in Steps 3 and 5. The extension methods do the same thing in one line each.

### 4. `FunctionInvocationDelegatingAgentBuilderExtensions.Use(...)` — the headline

This is the seam we haven't shown until now. It registers middleware that runs **around every tool call**:

```csharp
.Use(async (agent, fnCtx, next, ct) => {
    // BEFORE the tool runs — inspect fnCtx.Function, fnCtx.Arguments
    var result = await next(fnCtx, ct);
    // AFTER the tool runs — inspect result, elapsed time, fnCtx.Exception
    return result;
});
```

It's the exact analog of ASP.NET Core's request middleware, applied to `AIFunction` invocations. The middleware runs for every tool call — workshop-original `read_file` / `bash` / `edit_file` AND framework-auto `load_skill` / `FileMemory_*` / `TodoList_*`.

## Code walkthrough

### Refactor — `Agent/AgentBuilder.cs`

Before (Steps 3 + 5 layered together):

```csharp
AIAgent inner = client.AsAIAgent(options);
inner = new LoggingAgent(inner, loggerFactory.CreateLogger("ClaudeChat.Agent"));
if (enableOtel)
{
    inner = new OpenTelemetryAgent(inner, OtelSourceName);
}
#pragma warning disable MAAI001
var wrapped = new ToolApprovalAgent(inner, JsonSerializerOptions.Default);
#pragma warning restore MAAI001
return new BuildResult(wrapped, todoProvider);
```

After:

```csharp
AIAgent inner = client.AsAIAgent(options);

var serviceProvider = new ServiceCollection().BuildServiceProvider();

var builder = new AIAgentBuilder(inner)
    .UseLogging(loggerFactory, _ => { });

if (enableOtel)
{
    builder = builder.UseOpenTelemetry(OtelSourceName, _ => { });
}

#pragma warning disable MAAI001
builder = builder
    .UseToolApproval(JsonSerializerOptions.Default)
    .Use(ToolTimingMiddleware);
#pragma warning restore MAAI001

var wrapped = builder.Build(serviceProvider);
return new BuildResult(wrapped, todoProvider);
```

Same behavior up to `.UseToolApproval`. The new line is `.Use(ToolTimingMiddleware)`.

A subtle thing: the order of `.UseX` calls is the order of wraps **inside-out**. `UseLogging` wraps the raw agent; `UseOpenTelemetry` wraps that; `UseToolApproval` wraps that; `Use(ToolTimingMiddleware)` wraps that. The outermost (last) is closest to the user; the innermost (first) is closest to the model. **Tool timing sits BELOW the approval gate** — denied calls don't get timed (they never execute), which is the right semantic.

### The new middleware — `AgentBuilder.ToolTimingMiddleware`

```csharp
private static async ValueTask<object?> ToolTimingMiddleware(
    AIAgent agent,
    FunctionInvocationContext fnCtx,
    Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
    CancellationToken ct)
{
    var sw = Stopwatch.StartNew();
    try
    {
        var result = await next(fnCtx, ct);
        sw.Stop();
        Console.WriteLine($"  → {sw.ElapsedMilliseconds}ms");
        return result;
    }
    catch
    {
        sw.Stop();
        Console.WriteLine($"  → {sw.ElapsedMilliseconds}ms (failed)");
        throw;
    }
}
```

Four arguments:

- **`agent`** — the wrapped agent; we don't need it here.
- **`fnCtx`** — `Microsoft.Extensions.AI.FunctionInvocationContext`. Carries the function being called (`fnCtx.Function`), the JSON arguments (`fnCtx.Arguments`), the result slot, and an exception slot. We don't inspect any of it for timing — start a watch, call `next`, stop the watch.
- **`next`** — the rest of the pipeline. Calling it runs the actual function plus any deeper middleware.
- **`ct`** — cancellation, threaded through.

The middleware **doesn't append to the existing `[tool: …]` bracket line** because that line was already flushed by the stream renderer when the model emitted the function-call content (before this middleware runs). We print on the next line, indented `  → 25ms`, to suggest "this is the result of the call above."

`(failed)` marker on the catch path so a tool that throws still gets timed and labeled. The throw is rethrown so the upstream error path keeps working.

## Verify

```bash
dotnet build && dotnet test --filter Category=Unit    # 153 tests (no new tests; see pitfalls)
```

Live smoke for the timing output:

```text
$ dotnet run
you > list the .md files at the repo root, then summarize TUTORIAL.md.
claude > [glob: pattern="*.md", root="."]

         [read_file: path="TUTORIAL.md"]
           → 25ms
           → 1ms
         Here are the .md files and the summary...
```

And the framework's auto-registered tools too:

```text
you > What commit prefix should I use here?
claude > [load_skill: skillName="repo-context"]
           → 3ms
         Based on the repo-context skill...
```

`load_skill` got timed. `FileMemory_*` and `TodoList_*` would too — same middleware seam, no per-provider opt-in needed.

## Pitfalls

### Parallel tool calls render confusingly

When the model emits multiple tool calls in the same stream chunk (parallel calls in one turn), the bracket lines all print first, then all the timing lines:

```text
[glob: pattern="*.md", root="."]

[read_file: path="TUTORIAL.md"]
  → 25ms      ← glob completed
  → 1ms       ← read_file completed
```

The bracket-vs-timing pairing isn't visually obvious for two reasons:

1. The stream renderer prints bracket lines as the *model* announces calls (before any of them runs).
2. The middleware prints timing lines as the *runtime* completes each call.

The two streams interleave at the runtime layer, not the rendering layer. To fix it properly we'd integrate ChatLoop with the middleware — hold each tool name in a per-call buffer and render `[tool: …] (N ms)` atomically — but that's a bigger refactor than this step warrants. **Sequential calls (the common case) render fine; parallel batches are honest-but-noisy. Documented behavior, not a bug.**

### No new unit tests in this step

The refactor preserves behavior, so the existing 153 tests cover it. The timing middleware itself is observability code that needs a real `FunctionInvocationContext` to exercise — constructing one outside the framework requires either an `InternalsVisibleTo` + mock factory or a full agent run. **Workshop-pragmatic choice: rely on the build (catches signature drift on `FunctionInvocationContext` or the `.Use` overload) plus the live smoke for behavior.** The chapter's verification step is the substitute for a unit test.

If you want to test middleware in this style for a production codebase, the path is: extract the timing logic to a non-static class with a clock dependency, inject the clock via the DI container we're already constructing, and the unit test injects a fake clock + a captured stdout. Stretch.

### The empty `ServiceCollection` is load-bearing

`.Build(serviceProvider)` requires a non-null `IServiceProvider`. If you pass null, the builder throws inside one of the `.Use` extension methods. The workshop's empty container is fine for now — none of the wrappers we attach need services. Future steps that want HTTP clients, secret stores, or test clocks injected will turn this one line into a real `ServiceCollection` with registrations.

This is the second time a forced API shape changed code structure (the first was `BuildResult` in Step 13). Both are honest seams: "MAF expects this thing here, so we have to make space for it" is better than working around it.

### Wrap order is implicit and matters

In the imperative chain, order was obvious (line ordering = wrap ordering). In the fluent chain, the `.UseX().UseX().UseX()` reads as a forward pipeline, but the actual semantics are **inside-out** — the first `.Use` wraps closest to the model, the last `.Use` wraps closest to the user.

The chapter's earlier-step framing is now embedded in a comment block at the top of `AgentBuilder.cs`. If you swap two `.UseX` calls, the wrap order changes — for example, `.UseToolApproval(...).UseLogging(...)` would log the *gate's* behavior (denials, auto-approves) instead of the *raw* model boundary. Either is defensible; the existing order is what the workshop ships.

### MAF API drift watch

`AIAgentBuilder`, its `.Use(...)` overloads, the `FunctionInvocationDelegatingAgentBuilderExtensions`, and `FunctionInvocationContext` (from `Microsoft.Extensions.AI`) are stable-shaped but the wider MAF preview surface still moves. The `#pragma warning disable MAAI001` covers `ToolApprovalAgent` (still experimental); the rest aren't gated by MAAI001 today, so a future rename would surface as a plain compile error instead of an experimental warning. **Build is the load-bearing check; if it succeeds, the chain is wired correctly.**

## Stretch

- **Color the timing output.** Dim cyan or dim gray for `→ N ms`. Use `MarkdownStreamRenderer`'s ANSI helper (Step 9). Disable in non-TTY mode.
- **Configurable threshold** — only print timings above N ms. Reduces noise for fast-and-frequent calls (`list_dir` runs in <1 ms typically). Wire via a new `agent.json` field: `middleware: { toolTimingThresholdMs: 5 }`.
- **Aggregate stats in a `/timing` slash command.** Collect per-tool totals over the session (count, sum, min, max, p50). The middleware just appends to an `Observability.ToolTimingAccumulator` that `/timing` formats. Same shape as `UsageAccumulator` from Step 5.
- **Inject `Stopwatch.GetTimestamp` via DI** so the middleware can be unit-tested with a fake clock. Tiny refactor, real test coverage.
- **Add a "tool taxonomy" middleware** that classifies each call (network/disk/llm/local) for cost attribution. Same `.Use(...)` shape; richer reporting.
- **Approval gate semantics for `.Use` ordering.** Move tool-timing ABOVE `.UseToolApproval` and observe what changes — denied calls would get timed at `0 ms` (the gate short-circuits before the function runs). Useful as a chapter exercise to make the wrap-order point concrete.

## Where the seams are

- **`AIAgentBuilder` is the workshop's first encounter with the framework's *fluent extension surface*.** Until Step 14, we had two extension surfaces: tools (Steps 1–4) and providers (Steps 10–13). Now there's a third — agent-wrapping middleware via the builder. Three orthogonal levers.
- **The function-invocation seam is its own concept inside that surface.** `FunctionInvocationDelegatingAgentBuilderExtensions.Use(...)` operates *below* agent-level middleware: it sees every individual tool call rather than every turn. Useful when the right granularity is per-tool (timing, redaction, retries on tool failures, etc.) rather than per-turn.
- **The empty `ServiceProvider` is a placeholder for a real DI container.** Today nothing uses it; future steps (HTTP clients for MCP in Step 16, sub-agents in Step 15) will grow it. The line is intentionally one of the more "stately" pieces of `AgentBuilder.cs` — it's the harness's ambient services seam.
- **Steps 11–14 form a coherent retroactive lesson.** Each step started with a probe that revealed the framework already shipped what I was about to hand-roll: `load_skill` (11), the `FileMemory_*` tools (12), the `TodoList_*` tools (13), and now the `AIAgentBuilder` chain itself (14). The workshop-level rule: **before writing any extension code that crosses a framework boundary, probe what the framework already provides at that boundary.** This applies whether you're writing a tool (probe via "list every tool"), a provider (probe via reflection on `Microsoft.Agents.AI.dll`), or — as in Step 14 — a wrap chain (probe via reflection for `*BuilderExtensions` types).
- **A follow-up `[doc]` commit will land retro-callouts in Steps 3 and 5's chapters** pointing readers at `.UseToolApproval` / `.UseLogging` / `.UseOpenTelemetry` as the production-shape equivalent, same pattern as the `FileAccessProvider` heads-up commit after Step 11.

## Next

→ **Step 15 — Sub-agents via `SubAgentsProvider`** *(planned)*: spawn a separate agent run to handle a sub-task. The probe-first rule applies as always: minimal wiring → "list every tool" smoke → see what auto-registers → design around what's actually there.
