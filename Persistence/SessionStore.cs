// =============================================================================
//  SessionStore — persistence for AgentSession + our metadata wrapper
//
//  MAF gives us SerializeSessionAsync / DeserializeSessionAsync — the round-trip
//  primitive. It does NOT track ids, timestamps, models, or previews. Anything
//  the --list and --resume UX needs comes from this file.
//
//  File layout (one per session at sessions/<id>.json):
//
//    {
//      "id":        "a3f7c102",
//      "createdAt": "2026-05-09T10:00:00Z",
//      "updatedAt": "2026-05-09T10:15:00Z",
//      "model":     "claude-haiku-4-5",
//      "preview":   "first user message snippet",
//      "session":   { ...framework's opaque JsonElement... }
//    }
//
//  The "session" field is opaque — we hand it back to DeserializeSessionAsync
//  untouched. Everything else is *our* metadata.
// =============================================================================

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;

namespace ClaudeChat.Persistence;

public record SessionMeta(
    string Id,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string Model,
    string? Preview,
    string Path);

public static class SessionStore
{
    // Mutable so tests can swap it for a temp directory; production code never
    // reassigns it. Keeping it a settable static field is the smallest seam
    // that avoids passing a directory through every call site.
    public static string Dir = "sessions";

    public static void EnsureDir()
    {
        Directory.CreateDirectory(Dir);
        // Sweep .json.tmp files left behind by interrupted SaveAsync calls;
        // SaveAsync now writes-then-renames, see the field note from 2026-05-16.
        foreach (var tmp in Directory.EnumerateFiles(Dir, "*.json.tmp"))
        {
            try { File.Delete(tmp); } catch { /* best-effort */ }
        }
    }

    // 4 random bytes → 8 hex chars (~4B space). We resolve by prefix on resume,
    // so users only ever type the first few — same as `git checkout abc`.
    public static string NewId() =>
        Convert.ToHexString(Guid.NewGuid().ToByteArray().AsSpan(0, 4)).ToLowerInvariant();

    public static async Task SaveAsync(
        string id, DateTime createdAt, string model, string? preview,
        AgentSession session, AIAgent agent)
    {
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
        // Atomic save: write to <id>.json.tmp, then rename. File.Move with
        // overwrite is atomic on POSIX (same filesystem). A SIGKILL mid-write
        // leaves the .tmp behind but the previous <id>.json intact — worst
        // case is an orphan tmp file, which EnsureDir() sweeps on startup.
        var finalPath = Path.Combine(Dir, id + ".json");
        var tmpPath = finalPath + ".tmp";
        await File.WriteAllTextAsync(tmpPath,
            node.ToJsonString(new() { WriteIndented = true }));
        File.Move(tmpPath, finalPath, overwrite: true);
    }

    public static async Task<AgentSession> LoadAsync(SessionMeta meta, AIAgent agent)
    {
        var obj = JsonNode.Parse(await File.ReadAllTextAsync(meta.Path))!.AsObject();
        var sessionElem = obj["session"]!.Deserialize<JsonElement>();
        return await agent.DeserializeSessionAsync(sessionElem);
    }

    public static IEnumerable<SessionMeta> Enumerate()
    {
        if (!Directory.Exists(Dir)) yield break;
        foreach (var path in Directory.EnumerateFiles(Dir, "*.json"))
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

    // Returns all matches. Caller decides what to do with 0 / 1 / many — we
    // don't print or exit from inside the store.
    public static IReadOnlyList<SessionMeta> FindByPrefix(string prefix) =>
        Enumerate()
            .Where(r => r.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
}
