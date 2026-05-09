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
//  See tutorial/00-baseline.md for the walkthrough.
// =============================================================================

using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;                  // AnthropicClient — from the `Anthropic` SDK package
using Microsoft.Agents.AI;        // AIAgent, AgentSession — from MAF Abstractions

// Where we keep one JSON file per session. Created on demand.
const string SessionsDir = "sessions";

// -----------------------------------------------------------------------------
//  Argument parsing
//
//  The CLI deliberately mirrors Claude Code / git: short flags, prefix-match on
//  --resume, --continue picks the most recent. We do this by hand because three
//  flags don't justify pulling in System.CommandLine.
// -----------------------------------------------------------------------------
string? resumeId = null;
bool continueLast = false;
bool listOnly = false;

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
        case "--help" or "-h":
            PrintHelp(); return 0;
        default:
            Console.Error.WriteLine($"Unknown arg: {args[i]}");
            PrintHelp(); return 1;
    }
}

Directory.CreateDirectory(SessionsDir);

// --list is a pre-auth path: it doesn't touch the API, so we run it before
// requiring an API key. Same for --help.
if (listOnly)
{
    var rows = EnumerateSessions().OrderByDescending(r => r.UpdatedAt).ToList();
    if (rows.Count == 0) { Console.WriteLine("No sessions."); return 0; }
    Console.WriteLine($"{"ID",-10}  {"Updated",-19}  {"Model",-22}  Preview");
    foreach (var r in rows)
        Console.WriteLine($"{r.Id,-10}  {r.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {r.Model,-22}  {r.Preview ?? "<empty>"}");
    return 0;
}

// -----------------------------------------------------------------------------
//  Build the agent
//
//  Two layers compose here:
//    1. AnthropicClient — the raw HTTP client from the Anthropic SDK.
//    2. AsAIAgent(...)  — the MAF extension that adapts that client into an
//                         AIAgent: the abstraction MAF uses everywhere.
//
//  Once we have an AIAgent we never speak Anthropic-specific types again. That's
//  the point of MAF: in later steps we'll add tools, approval gates, logging,
//  compaction — all written against AIAgent, all provider-agnostic.
// -----------------------------------------------------------------------------
var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine("Set ANTHROPIC_API_KEY before running.");
    return 1;
}

var model = Environment.GetEnvironmentVariable("ANTHROPIC_DEPLOYMENT_NAME")
            ?? "claude-haiku-4-5";

AnthropicClient client = new() { ApiKey = apiKey };

AIAgent agent = client.AsAIAgent(
    model: model,
    name: "ClaudeChat",
    instructions: "You are a helpful assistant. Keep replies concise.");

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
    toLoad = ResolveByPrefix(resumeId);
    if (toLoad == null) { Console.Error.WriteLine($"No session matches '{resumeId}'."); return 1; }
}
else if (continueLast)
{
    toLoad = EnumerateSessions().OrderByDescending(r => r.UpdatedAt).FirstOrDefault();
    if (toLoad == null) Console.WriteLine("No previous sessions found, starting new.");
}

if (toLoad != null)
{
    // The framework gives us back exactly the JsonElement we saved earlier.
    // Our metadata wrapper (id/createdAt/preview/...) is layered around it.
    var obj = JsonNode.Parse(await File.ReadAllTextAsync(toLoad.Path))!.AsObject();
    var sessionElem = obj["session"]!.Deserialize<JsonElement>();
    session = await agent.DeserializeSessionAsync(sessionElem);
    sessionId = toLoad.Id;
    createdAt = toLoad.CreatedAt;
    preview = toLoad.Preview;
    Console.WriteLine($"Resumed session {sessionId} — {preview ?? "<empty>"}");
}
else
{
    sessionId = NewId();
    // CreateSessionAsync is the MAF idiom for "fresh conversation state".
    // (In the prerelease docs you may see GetNewThread() — that's an older name.)
    session = await agent.CreateSessionAsync();
    createdAt = DateTime.UtcNow;
    preview = null;
    Console.WriteLine($"Started new session: {sessionId}");
}

Console.WriteLine($"Model: {model}. Commands: 'exit' (quit), 'clear' (new session), '/id' (show id).\n");

// -----------------------------------------------------------------------------
//  The chat loop
//
//  Each iteration:
//    1. Read a user line.
//    2. Handle harness-level commands (exit / clear / /id) without round-tripping.
//    3. Stream a turn via RunStreamingAsync.
//    4. Persist the session to disk.
//
//  Persisting per turn (rather than only on exit) means Ctrl+C can't lose state.
// -----------------------------------------------------------------------------
while (true)
{
    Console.Write("you > ");
    var input = Console.ReadLine();
    if (input is null) break;                                          // Ctrl+D
    if (input.Equals("exit", StringComparison.OrdinalIgnoreCase)) break;
    if (input.Equals("quit", StringComparison.OrdinalIgnoreCase)) break;
    if (string.IsNullOrWhiteSpace(input)) continue;

    if (input.Equals("/id", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"(session: {sessionId})\n");
        continue;
    }

    if (input.Equals("clear", StringComparison.OrdinalIgnoreCase))
    {
        // Mint a new session in-place. The previous one stays on disk —
        // unlike `rm session.json`, this is non-destructive.
        sessionId = NewId();
        session = await agent.CreateSessionAsync();
        createdAt = DateTime.UtcNow;
        preview = null;
        Console.WriteLine($"(new session: {sessionId})\n");
        continue;
    }

    // Streaming turn. RunStreamingAsync returns IAsyncEnumerable<AgentResponseUpdate>.
    // In Step 0 each update only carries text; in later steps it'll also carry
    // tool-call requests, tool results, reasoning ("thinking") content, etc.
    Console.Write("claude > ");
    await foreach (var update in agent.RunStreamingAsync(input, session))
        Console.Write(update.Text);
    Console.WriteLine("\n");

    // Lazily set a preview the first time we have user input — this is what
    // shows up in `--list` so you can recognize the session.
    preview ??= input.Length > 60 ? input[..60] + "..." : input;
    await SaveSession(sessionId, createdAt, model, preview, session, agent);
}

Console.WriteLine($"\n(session saved: {sessionId} — resume with: dotnet run -- --resume {sessionId})");
return 0;

// =============================================================================
//  Helpers — session storage
//
//  We store one file per session at sessions/<id>.json with this shape:
//
//    {
//      "id":        "a3f7c102",
//      "createdAt": "2026-05-09T10:00:00Z",
//      "updatedAt": "2026-05-09T10:15:00Z",
//      "model":     "claude-haiku-4-5",
//      "preview":   "first user message snippet for --list",
//      "session":   { ...the framework's opaque JsonElement... }
//    }
//
//  The "session" field is the framework's own blob — we treat it as opaque and
//  hand it back to DeserializeSessionAsync untouched. Everything else is *our*
//  metadata, added so we can implement --list and prefix-resume. MAF doesn't
//  know or care about it.
// =============================================================================

// Short, unambiguous-in-practice ids (4 random bytes → 8 hex chars, ~4B space).
// We use prefix-matching on resume so users only need to type the first few.
static string NewId() =>
    Convert.ToHexString(Guid.NewGuid().ToByteArray().AsSpan(0, 4)).ToLowerInvariant();

static async Task SaveSession(string id, DateTime createdAt, string model, string? preview,
                              AgentSession session, AIAgent agent)
{
    // SerializeSessionAsync returns a JsonElement. We embed it under "session"
    // and add our metadata around it. JsonNode lets us mix-and-match cleanly.
    var snapshot = await agent.SerializeSessionAsync(session);
    var node = new JsonObject
    {
        ["id"]        = id,
        ["createdAt"] = createdAt,
        ["updatedAt"] = DateTime.UtcNow,
        ["model"]     = model,
        ["preview"]   = preview,
        ["session"]   = JsonNode.Parse(snapshot.GetRawText()),
    };
    await File.WriteAllTextAsync(Path.Combine(SessionsDir, id + ".json"),
                                 node.ToJsonString(new() { WriteIndented = true }));
}

static IEnumerable<SessionMeta> EnumerateSessions()
{
    if (!Directory.Exists(SessionsDir)) yield break;
    foreach (var path in Directory.EnumerateFiles(SessionsDir, "*.json"))
    {
        SessionMeta? meta = null;
        try
        {
            var obj = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
            if (obj == null) continue;
            meta = new SessionMeta(
                (string)obj["id"]!,
                (DateTime)obj["createdAt"]!,
                (DateTime)obj["updatedAt"]!,
                (string)obj["model"]!,
                (string?)obj["preview"],
                path);
        }
        catch { /* ignore malformed files; they're someone else's problem */ }
        if (meta != null) yield return meta;
    }
}

// Prefix-match like git: unambiguous prefix wins, ambiguous fails loudly.
static SessionMeta? ResolveByPrefix(string prefix)
{
    var matches = EnumerateSessions()
        .Where(r => r.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        .ToList();
    if (matches.Count == 0) return null;
    if (matches.Count > 1)
    {
        Console.Error.WriteLine($"Ambiguous prefix '{prefix}' matches:");
        foreach (var m in matches) Console.Error.WriteLine($"  {m.Id}  {m.Preview}");
        Environment.Exit(1);
    }
    return matches[0];
}

static void PrintHelp()
{
    Console.WriteLine("""
        Usage:
          dotnet run                          Start a new session
          dotnet run -- --continue            Resume the most recent session
          dotnet run -- --resume <id-prefix>  Resume a specific session (prefix match)
          dotnet run -- --list                List all sessions and exit
          dotnet run -- --help                Show this help
        """);
}

// In-file record type. Top-level statements + types-below is fine in C# 9+.
record SessionMeta(string Id, DateTime CreatedAt, DateTime UpdatedAt, string Model, string? Preview, string Path);
