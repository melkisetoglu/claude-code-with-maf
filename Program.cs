// =============================================================================
//  Microsoft Agent Framework + Claude — Workshop tutorial
//  Step 0: Baseline. Streaming chat, named persistent sessions.
//
//  Concepts introduced in this step:
//    - AnthropicClient        (from the official Anthropic SDK package)
//    - AsAIAgent()            (extension that wraps the SDK as a MAF AIAgent)
//    - AIAgent                (the MAF abstraction we'll build everything on)
//    - AgentSession           (MAF's conversation-state container)
//    - SerializeSessionAsync / DeserializeSessionAsync   (round-trip to JSON)
//    - RunStreamingAsync      (streamed turn → IAsyncEnumerable<update>)
//
//  This file is the entry point only: arg parsing, --list/--help dispatch, and
//  resolving which session to use. The actual interactive loop lives in
//  Harness/ChatLoop.cs; agent construction in Agent/AgentBuilder.cs; session
//  persistence in Persistence/SessionStore.cs.
//
//  See tutorial/00-baseline.md for the walkthrough.
// =============================================================================

using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Trace;
using ClaudeChat.Agent;
using ClaudeChat.Config;
using ClaudeChat.Harness;
using ClaudeChat.Harness.Commands;
using ClaudeChat.Observability;
using ClaudeChat.Persistence;

// -----------------------------------------------------------------------------
//  Argument parsing
//
//  The CLI deliberately mirrors Claude Code / git: short flags, prefix-match on
//  --resume, --continue picks the most recent. We do this by hand because three
//  flags don't justify pulling in System.CommandLine.
// -----------------------------------------------------------------------------
string? resumeId = null;
string? configPath = null;
bool continueLast = false;
bool listOnly = false;
bool enableOtel = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--continue" or "-c":
            continueLast = true; break;
        case "--resume" or "-r":
            if (i + 1 >= args.Length) { Console.Error.WriteLine("--resume requires <id>"); return 1; }
            resumeId = args[++i]; break;
        case "--list" or "-l":
            listOnly = true; break;
        case "--otel":
            enableOtel = true; break;
        case "--config":
            if (i + 1 >= args.Length) { Console.Error.WriteLine("--config requires <path>"); return 1; }
            configPath = args[++i]; break;
        case "--help" or "-h":
            PrintHelp(); return 0;
        default:
            Console.Error.WriteLine($"Unknown arg: {args[i]}");
            PrintHelp(); return 1;
    }
}

SessionStore.EnsureDir();

// --list is a pre-auth path: it doesn't touch the API, so we run it before
// requiring an API key. Same for --help.
if (listOnly)
{
    var rows = SessionStore.Enumerate().OrderByDescending(r => r.UpdatedAt).ToList();
    if (rows.Count == 0) { Console.WriteLine("No sessions."); return 0; }
    Console.WriteLine($"{"ID",-10}  {"Updated",-19}  {"Model",-22}  Preview");
    foreach (var r in rows)
        Console.WriteLine($"{r.Id,-10}  {r.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {r.Model,-22}  {r.Preview ?? "<empty>"}");
    return 0;
}

// -----------------------------------------------------------------------------
//  Build the agent
// -----------------------------------------------------------------------------
// Trim env vars: a trailing \n from `export FOO=$(cat keyfile)` or a paste with
// a line break would otherwise propagate into the x-api-key HTTP header and
// blow up with "New-line characters are not allowed in header values".
var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")?.Trim();
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine("Set ANTHROPIC_API_KEY before running.");
    return 1;
}

// -----------------------------------------------------------------------------
//  Configuration — Step 6
//
//  Explicit --config beats auto-discovered ./agent.json. Either is optional;
//  out of the box (no config file, no flag) the agent uses built-in defaults
//  identical to Step 5 behaviour.
//
//  The model is resolved with this precedence:
//    explicit env ANTHROPIC_DEPLOYMENT_NAME → config.model → built-in default
//  Env wins because it's the most explicit / per-invocation knob.
// -----------------------------------------------------------------------------
AgentConfig? agentConfig;
try
{
    agentConfig = configPath != null
        ? AgentConfigLoader.LoadFromPath(configPath)
        : AgentConfigLoader.TryLoadFromCwd();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to load config: {ex.Message}");
    return 1;
}

var modelEnv = Environment.GetEnvironmentVariable("ANTHROPIC_DEPLOYMENT_NAME")?.Trim();
// Effective model surfaced to ChatLoop (for the per-turn cost line and
// the session metadata). Mirrors AgentBuilder's resolution.
var model = !string.IsNullOrEmpty(modelEnv)
    ? modelEnv
    : agentConfig?.Model ?? AgentBuilder.DefaultModel;

// -----------------------------------------------------------------------------
//  Observability — Step 5
//
//  Logging: a custom JSON-Lines file writer (Observability/FileLogger.cs).
//  Level controlled by CLAUDECHAT_LOG_LEVEL (default Debug).
//
//  Tracing: OpenTelemetry with the console exporter, only when --otel was
//  passed. The console exporter dumps spans into stdout, which is noisy but
//  the only zero-config exporter that works without an external collector.
//  Swap for OTLP in production.
// -----------------------------------------------------------------------------
// Default Debug because MAF's LoggingAgent emits its per-Run* trace at
// Debug; Information would leave the log file empty in normal use, which
// defeats the workshop's point. Override via env if you want it quieter.
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

// Step 16 fix: ApprovalState moves out of ChatLoop and into Program.cs so
// AgentBuilder can capture it for the MCP approval middleware. The state is
// shared between the workshop's existing approval gate (via ToolApprovalAgent
// + ApprovalRequiredAIFunction for workshop tools) and the new function-
// invocation middleware that gates MCP tools (which can't use the framework's
// approval flow — see Step 16 chapter for the diagnosis).
var approvalState = new ApprovalState();

AgentBuilder.BuildResult built;
try
{
    built = AgentBuilder.Build(
        apiKey,
        agentConfig,
        loggerFactory,
        approvalState,
        enableOtel,
        modelOverride: !string.IsNullOrEmpty(modelEnv) ? modelEnv : null);
}
catch (InvalidOperationException ex)   // unknown tool / invalid requireApproval
{
    Console.Error.WriteLine($"agent.json config error: {ex.Message}");
    return 1;
}
// Pull out the pieces. Most of the code below still wants the bare AIAgent;
// only the chat loop needs the TodoProvider reference (for /todos).
AIAgent agent = built.Agent;

// -----------------------------------------------------------------------------
//  Resolve which session to use
//
//  Three paths:
//    --resume <prefix>  → look it up, fail if missing or ambiguous
//    --continue         → most-recently-updated session
//    (none)             → fresh session, mint a new id
// -----------------------------------------------------------------------------
string sessionId;
AgentSession session;
DateTime createdAt;
string? preview;

SessionMeta? toLoad = null;
if (resumeId != null)
{
    var matches = SessionStore.FindByPrefix(resumeId);
    if (matches.Count == 0)
    {
        Console.Error.WriteLine($"No session matches '{resumeId}'.");
        return 1;
    }
    if (matches.Count > 1)
    {
        Console.Error.WriteLine($"Ambiguous prefix '{resumeId}' matches:");
        foreach (var m in matches) Console.Error.WriteLine($"  {m.Id}  {m.Preview}");
        return 1;
    }
    toLoad = matches[0];
}
else if (continueLast)
{
    toLoad = SessionStore.Enumerate().OrderByDescending(r => r.UpdatedAt).FirstOrDefault();
    if (toLoad == null) Console.WriteLine("No previous sessions found, starting new.");
}

if (toLoad != null)
{
    session = await SessionStore.LoadAsync(toLoad, agent);
    sessionId = toLoad.Id;
    createdAt = toLoad.CreatedAt;
    preview = toLoad.Preview;
    Console.WriteLine($"Resumed session {sessionId} — {preview ?? "<empty>"}");
}
else
{
    sessionId = SessionStore.NewId();
    // CreateSessionAsync is the MAF idiom for "fresh conversation state".
    // (In the prerelease docs you may see GetNewThread() — that's an older name.)
    session = await agent.CreateSessionAsync();
    createdAt = DateTime.UtcNow;
    preview = null;
    Console.WriteLine($"Started new session: {sessionId}");
}

await ChatLoop.RunAsync(agent, model, sessionId, session, createdAt, preview, agentConfig, built.Todos, approvalState, built.Governance, built.AuditTrail);
tracerProvider?.Dispose();
return 0;

static void PrintHelp()
{
    Console.WriteLine("""
        Usage:
          dotnet run                          Start a new session
          dotnet run -- --continue            Resume the most recent session
          dotnet run -- --resume <id-prefix>  Resume a specific session (prefix match)
          dotnet run -- --list                List all sessions and exit
          dotnet run -- --config <path>       Load agent config from a specific path (Step 6)
          dotnet run -- --otel                Enable OpenTelemetry console exporter (Step 5)
          dotnet run -- --help                Show this help
        Config:
          ./agent.json                        Auto-discovered if present; --config overrides
        Env:
          ANTHROPIC_API_KEY                   Required
          ANTHROPIC_DEPLOYMENT_NAME           Model id, overrides agent.json model
          CLAUDECHAT_LOG_LEVEL                Trace|Debug|Information|Warning|Error, default Debug
        """);
}
