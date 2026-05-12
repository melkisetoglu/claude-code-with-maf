# Step 05 — Logging, OpenTelemetry, per-turn token/cost

> *Goal: know what the agent did, how often, and what it cost — before the harness gets too complex to debug without it.*

> **Retrospective heads-up (added after Step 14).** This step hand-wires `LoggingAgent` and `OpenTelemetryAgent` with raw constructor calls (`new LoggingAgent(inner, logger)` and `new OpenTelemetryAgent(inner, source)`). MAF ships `LoggingAgentBuilderExtensions.UseLogging(loggerFactory, configure)` and `OpenTelemetryAgentBuilderExtensions.UseOpenTelemetry(sourceName, configure)` on the `AIAgentBuilder` fluent pipeline — same outcomes, one line each. We didn't know until Step 14, which refactors the wrap chain to the builder shape. **This chapter stands as-is** — the imperative wrap-and-reassign form makes the delegating-agent layering visible in a way the fluent form hides, and that's worth seeing once before trusting the sugar. In production code, prefer the fluent form. Full discussion in [Step 14's chapter](14-middleware.md).

Steps 6 onwards will pile on configuration, slash commands, plan mode, compaction, memory, sub-agents, MCP. Debugging any of that without a log is misery, and adding observability after-the-fact means you can't go back and ask *what happened last Tuesday?* Step 5 lands the cheapest scaffolding that answers that question.

Three things ship together because they share a theme but live at different layers:

| Layer | Concern | What it gives you |
|---|---|---|
| **Logging** (`LoggingAgent`) | Structured per-call trace | A file you can `grep` later. Every `Run*` call, plus exceptions, plus anything the inner agent's logger emits. |
| **Tracing** (`OpenTelemetryAgent`) | Distributed-style span emission | Spans you can pipe into Jaeger / Honeycomb / Datadog in production. For the workshop, a console exporter dumps them to stdout. |
| **Token / cost** (`UsageContent`) | Per-turn token use + dollar estimate | One line after each turn — `(turn: 1234 in + 567 out, $0.0042)` — so the cost of asking the model anything is visible. |

It's also the workshop's third **delegating-agent** lesson, after Step 3's `ToolApprovalAgent`. Three different concerns, same MAF pattern.

## What you'll have at the end

```
$ dotnet run
Started new session: 7065ad5c
Model: claude-haiku-4-5. Commands: '/exit' (quit), '/clear' (new session), '/id' (show id).

you > reply with a single word: hello
claude > hello
(turn: 2036 in + 4 out, $0.0021)

you > /exit

$ tail -3 claudechat.log
{"ts":"2026-05-11T07:21:46.853Z","level":"Debug","cat":"ClaudeChat.Agent","msg":"RunStreamingAsync invoked."}
{"ts":"2026-05-11T07:21:49.789Z","level":"Debug","cat":"ClaudeChat.Agent","msg":"RunStreamingAsync completed."}
```

```
$ dotnet run -- --otel
you > what's the capital of France?
claude > Paris is the capital of France.
Activity.TraceId:            dac73c00af21a3623213f3eeb077308f
Activity.SpanId:             78349f24b380e83a
Activity.DisplayName:        invoke_agent ClaudeChat(01690223e88f4fbd99ab0c6027e31d44)
Activity.StartTime:          2026-05-11T07:22:01.7912240Z
Activity.Duration:           00:00:01.6030170
(turn: 13 in + 9 out, $0.0001)
```

## MAF concepts introduced

| Concept | What it is |
|---|---|
| **`LoggingAgent`** | A `DelegatingAIAgent` that logs every `Run*` call (start + completion + exceptions) at Debug level via an `ILogger`. Construction: `new LoggingAgent(inner, logger)`. |
| **`OpenTelemetryAgent`** | A `DelegatingAIAgent` that emits an `Activity` (span) for each call. Construction: `new OpenTelemetryAgent(inner, sourceName)`. Has `EnableSensitiveData` opt-in to include tool args. |
| **`UsageContent` / `UsageDetails`** | An `AIContent` the model emits in the stream at end-of-turn. `Details.InputTokenCount`, `OutputTokenCount`, etc. The harness reads this; the model never sees it again. |
| **`ILoggerFactory` + `ILoggerProvider`** | The standard .NET pluggable logging contract. We write a tiny custom `FileLoggerProvider` (~50 LOC) rather than pulling Serilog — one teaching moment, zero new deps. |
| **`TracerProvider` + `ActivitySource`** | The OpenTelemetry SDK shape. We build one in `Program.cs` with the console exporter, registered against the `ClaudeChat` source name. |

## Setup

Two new packages for OpenTelemetry. Logging stays no-new-deps.

```bash
dotnet add package OpenTelemetry
dotnet add package OpenTelemetry.Exporter.Console
```

One new folder, four new files:

```
Observability/FileLogger.cs        — custom ILoggerProvider writing JSON-Lines
Observability/Pricing.cs           — hardcoded $/MTok rates per model
Observability/TurnUsage.cs         — token accumulator across approval round-trips
.gitignore                          + *.log     (we don't commit claudechat.log)
```

## Walkthrough

### Custom JSON-Lines file logger

*In [`Observability/FileLogger.cs`](../Observability/FileLogger.cs).*

The `ILoggerProvider` / `ILogger` contract is small enough to teach in 50 lines. Each record looks like:

```json
{"ts":"2026-05-11T07:21:46.853Z","level":"Debug","cat":"ClaudeChat.Agent","msg":"RunStreamingAsync invoked."}
```

The custom provider exists to keep dependencies tight (no Serilog package), give us a workshop-visible implementation, and demonstrate that the .NET logging contract is genuinely small. **A real deployment would use Serilog or OpenTelemetry Logs and gain rotation, structured templates, sinks. We get a line per record into one file. Adequate.**

```csharp
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();
    private readonly LogLevel _minLevel;

    public FileLoggerProvider(string path, LogLevel minLevel) { /* ...AutoFlush=true... */ }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(string categoryName, LogLevel level, string message) { /* JSON line, lock */ }
}
```

The inner `FileLogger` is a sealed nested type — only the provider can mint them. Concurrency: a single `lock` per writer. Fine for a single chat REPL; a high-throughput server would use a `Channel<T>` + background drain instead.

### Pricing table + cost math

*In [`Observability/Pricing.cs`](../Observability/Pricing.cs).*

```csharp
private static readonly Dictionary<string, Rates> Table = new(StringComparer.OrdinalIgnoreCase)
{
    ["claude-haiku-4-5"]  = new Rates(1.00m, 5.00m),
    ["claude-sonnet-4-6"] = new Rates(3.00m, 15.00m),
};

public static decimal? CostUsd(string model, long inputTokens, long outputTokens)
{
    var rates = Lookup(model);
    if (rates is null) return null;
    return inputTokens  / 1_000_000m * rates.Value.InputPerMTok
         + outputTokens / 1_000_000m * rates.Value.OutputPerMTok;
}
```

Hardcoded `decimal` math, returns `null` for unknown models (which we render as `$?`). **Workshop scope**: a production deployment would source these from config so a price change doesn't require a redeploy. The chapter spells out this trade-off because the alternative — fancy live-pricing lookup — would teach a different lesson.

### Per-turn token accumulator

*In [`Observability/TurnUsage.cs`](../Observability/TurnUsage.cs).*

Why a tiny class instead of two `long`s inline:

> "A turn can span multiple `RunStreamingAsync` calls (approval round-trips), each of which may emit its own `UsageContent` — we add them all together so the per-turn line is honest."

A turn with one tool call → at least two underlying `RunStreamingAsync` invocations (one for the model's call, one for the model's reply after the approval response). Each emits its own `UsageContent`. The accumulator just sums them. **Naming the concept now buys you Step 17 (budgets) when you'll want to subtract from a remaining-budget counter.**

Locale gotcha: the formatter forces `CultureInfo.InvariantCulture` for the decimal. Without it, machines in non-`en-US` locales render `$0,0021` (Turkish, Turkish lira-style comma) instead of `$0.0021`. **The number you display has a culture; pick one explicitly when it goes to a user-facing line.**

### Wiring it up in `AgentBuilder`

*In [`Agent/AgentBuilder.cs`](../Agent/AgentBuilder.cs).*

```csharp
public static AIAgent Build(
    string apiKey,
    string model,
    ILoggerFactory loggerFactory,
    bool enableOtel = false)
{
    AnthropicClient client = new() { ApiKey = apiKey };

    AIAgent inner = client.AsAIAgent(...tools...);

    // Step 5: log every Run* call at the model boundary.
    inner = new LoggingAgent(inner, loggerFactory.CreateLogger("ClaudeChat.Agent"));

    // Step 5 (opt-in): tracing.
    if (enableOtel)
    {
        inner = new OpenTelemetryAgent(inner, OtelSourceName);
    }

#pragma warning disable MAAI001
    return new ToolApprovalAgent(inner, JsonSerializerOptions.Default);
#pragma warning restore MAAI001
}
```

**Wrap order is inside-out: raw model → `LoggingAgent` → `OpenTelemetryAgent` → `ToolApprovalAgent`.** Step 3's chapter promised this order — "outside is closer to the user" — and Step 5 honours it:

- `LoggingAgent` sits directly above the model so logs capture *raw* `RunStreamingAsync` calls, including the approval-response messages the gate forwards back. If we logged above the gate, denied calls would still appear as "model invoked" — confusing.
- `OpenTelemetryAgent` sits above logging because spans benefit from logs being already captured (you'd correlate a trace ID to log lines, not the other way round). It's also conditional: no spans, no `ActivitySource` — no overhead.
- `ToolApprovalAgent` stays outermost. It's the user-visible boundary.

The order isn't religion — there are tradeoffs the other way — but a clear default beats an ad-hoc one. Step 14 (hooks/middleware) revisits this when more delegators want to slot in.

### Configuration in `Program.cs`

*In [`Program.cs`](../Program.cs).*

```csharp
var logLevelEnv = Environment.GetEnvironmentVariable("CLAUDECHAT_LOG_LEVEL")?.Trim();
var logLevel = Enum.TryParse<LogLevel>(logLevelEnv, ignoreCase: true, out var lvl)
    ? lvl : LogLevel.Debug;

using var fileLoggerProvider = new FileLoggerProvider("claudechat.log", logLevel);
using var loggerFactory = LoggerFactory.Create(b => b
    .SetMinimumLevel(logLevel)
    .AddProvider(fileLoggerProvider));

TracerProvider? tracerProvider = null;
if (enableOtel)
{
    tracerProvider = Sdk.CreateTracerProviderBuilder()
        .AddSource(AgentBuilder.OtelSourceName)
        .AddConsoleExporter()
        .Build();
}

AIAgent agent = AgentBuilder.Build(apiKey, model, loggerFactory, enableOtel);
```

Default log level: **Debug**, not Information. Why? `LoggingAgent` emits its per-call trace at Debug. Default Information would leave the log file empty in normal use, which defeats the workshop's whole point. Override via `CLAUDECHAT_LOG_LEVEL=Information` if you want it quieter.

The OpenTelemetry setup: only when `--otel` is passed. The console exporter is *very noisy* — spans interleave with chat output — so it stays opt-in. Real deployments would swap `AddConsoleExporter()` for `AddOtlpExporter(o => o.Endpoint = new Uri("..."))` aimed at Jaeger or a collector.

### Reading `UsageContent` in `ChatLoop`

*In [`Harness/ChatLoop.cs`](../Harness/ChatLoop.cs).*

One new case in the content switch:

```csharp
case UsageContent uc:
    usage.Add(uc.Details);
    break;
```

And a one-line summary after the inner streaming loop ends:

```csharp
Console.WriteLine();
Console.WriteLine(usage.FormatSummary(model));
```

The `usage` accumulator lives outside the inner `while (true)` loop, so it captures totals across every approval round-trip. **Per turn, not per HTTP request** — that's the user-meaningful unit.

## Verify

```bash
dotnet build
dotnet test                                # 83 unit tests (15 new across Pricing + TurnUsage)
```

Then a live turn:

```bash
rm -f claudechat.log    # start clean
dotnet run
you > reply with a single word: hello
claude > hello
(turn: 2036 in + 4 out, $0.0021)
you > /exit
```

Inspect:

```bash
head claudechat.log
# {"ts":"...","level":"Debug","cat":"ClaudeChat.Agent","msg":"RunStreamingAsync invoked."}
# {"ts":"...","level":"Debug","cat":"ClaudeChat.Agent","msg":"RunStreamingAsync completed."}
```

Try the OpenTelemetry flag:

```bash
dotnet run -- --otel
you > what's the capital of France?
# Expect: Activity.TraceId/SpanId/DisplayName lines interleaved with the reply.
```

Then try lowering verbosity:

```bash
CLAUDECHAT_LOG_LEVEL=Information dotnet run
you > anything
# Expect: claudechat.log gains 0 lines (LoggingAgent's trace is Debug-only).
```

## Pitfalls

### Default log level matters more than you'd think

If `LoggingAgent` logs at Debug and your default level is Information, the log file is empty. Users will conclude "logging is broken" rather than "filtered out." **Either match the default to the most-useful framework level, or document the env var loudly.** We default to Debug.

### Pricing tables go stale

Anthropic (and every provider) changes prices. A hardcoded table will be wrong eventually. **Treat it as a workshop simplification, not a primitive.** A real implementation either ships with config-driven pricing or fetches live. The chapter says so out loud.

### Locale leaks into user-facing numbers

`$0,0021` happens silently on Turkish, German, French locales because the decimal separator is a comma. **`ToString("0.0000")` uses `CurrentCulture` by default in .NET.** Force `CultureInfo.InvariantCulture` whenever the string will be read by a human in another country (or by `grep`, or by JSON readers). We do.

### Console exporter is noisy

`AddConsoleExporter()` interleaves span dumps with whatever else is going to stdout — including the chat. Workshop-fine because it's *visible*, but unsuitable for a real terminal UX. Step 9 (streaming polish) could route OTel output elsewhere or buffer it; Step 16 (MCP) might pipe to a real collector. Today: live with it, or don't pass `--otel`.

### Wrap order isn't accidental

`LoggingAgent` inside, `ToolApprovalAgent` outside. Reversed, the log would record denied tool calls as "tool ran" because the gate already stripped the deny by the time the log fires. **Outer is closer to the human; inner is closer to the model.**

### `LoggingAgent` doesn't capture tool *bodies*

It logs the boundary calls (`RunStreamingAsync invoked / completed`), not what tools did inside. To see tool execution you'd add `EnableSensitiveData = true` on `OpenTelemetryAgent` (it'll embed tool args in span tags) or log inside the tool functions themselves. Not worth doing today.

### Token counts come from the model, not us

`UsageContent.Details.InputTokenCount` is whatever the model server reports — we don't double-count or verify. If the model decides to charge differently than its published rate, our cost line is wrong but in a predictable direction. **The display is an estimate, not an invoice.**

## Stretch exercises

- **Rotate `claudechat.log`** when it crosses some size (1MB?) — rename to `claudechat.log.1`, start a new file.
- **`/cost` slash command** showing the running total for the current session. Sets up Step 7.
- **Reuse `LoggingAgent` for the harness too** — log every `/clear`, `/exit`, `ApprovalPrompt.Ask`, with the same JSON-Lines provider.
- **Plug Serilog in**. Drop `FileLogger.cs`, add `Serilog.Extensions.Logging` + `Serilog.Sinks.File`. Compare LOC and capabilities.
- **OTLP exporter**. Swap `AddConsoleExporter()` for `AddOtlpExporter(o => o.Endpoint = ...)`, run a local Jaeger via `docker run jaegertracing/all-in-one`, see your spans in a real UI.
- **Track per-tool cost** — separately accumulate tokens spent inside tool-driven turns vs. plain chat. Bias the model toward cheaper tools.

## Where the seams are

What this step deliberately doesn't have:

- **No log rotation.** Single growing file. Stretch exercise.
- **No metrics / OTel meters.** Spans only. A real implementation would also emit counters (`turns_total`, `cost_usd_sum`).
- **No /cost slash command.** Step 7.
- **No structured templates** in log messages (`{ToolName} called with {Args}`) — we get whatever `LoggingAgent` writes. Serilog would unlock this.
- **No alerting / budgets.** Step 17.
- **No span attribute customization** — using framework defaults. `EnableSensitiveData` is a one-liner away if you want it.

## Next

→ **Step 06 — external `agent.json`: model, system prompt, tool allowlist, approval rules** *(planned)*

We've got logs, cost, traces. Step 6 is the first time the agent's *behaviour* (model, prompt, gating) becomes data rather than code — a config file you can edit without recompiling. After Step 6 you can have multiple personalities of the same agent without forking the project.
