// =============================================================================
//  SessionStoreTests — covers the agent-free parts of SessionStore:
//    - NewId shape
//    - Enumerate over a directory of JSON files (skipping malformed)
//    - FindByPrefix: 0 / 1 / many matches
//
//  We don't test SaveAsync / LoadAsync here because those need an AIAgent
//  (for SerializeSessionAsync / DeserializeSessionAsync). Those tests come
//  in the testing-interlude step, when we build a fake AIAgent.
//
//  Each test sets SessionStore.Dir to a unique temp directory, runs, then
//  cleans up. The class is in a non-parallel collection so SessionStore.Dir
//  isn't mutated under another test's feet.
// =============================================================================

using System.Text.Json.Nodes;
using ClaudeChat.Persistence;

namespace ClaudeChat.Tests;

[Trait("Category", "Unit")]
[Collection("SessionStore-shared-static")]
public sealed class SessionStoreTests : IDisposable
{
    private readonly string _previousDir;
    private readonly string _tmp;

    public SessionStoreTests()
    {
        _previousDir = SessionStore.Dir;
        _tmp = Path.Combine(Path.GetTempPath(), "claudechat-sessions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
        SessionStore.Dir = _tmp;
    }

    public void Dispose()
    {
        SessionStore.Dir = _previousDir;
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void NewId_returns_8_lowercase_hex_chars()
    {
        var id = SessionStore.NewId();

        Assert.Equal(8, id.Length);
        Assert.All(id, c => Assert.True((c is >= '0' and <= '9') || (c is >= 'a' and <= 'f'),
            $"unexpected char '{c}' in id '{id}'"));
    }

    [Fact]
    public void Enumerate_returns_empty_for_empty_dir()
    {
        Assert.Empty(SessionStore.Enumerate());
    }

    [Fact]
    public void Enumerate_returns_metadata_from_well_formed_files()
    {
        WriteFakeSession("a3f7c102", "claude-haiku-4-5", "what's a monoid?");
        WriteFakeSession("b1c2d3e4", "claude-sonnet-4-6", "explain MAF");

        var rows = SessionStore.Enumerate().ToList();

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Id == "a3f7c102" && r.Preview == "what's a monoid?");
        Assert.Contains(rows, r => r.Id == "b1c2d3e4" && r.Model == "claude-sonnet-4-6");
    }

    [Fact]
    public void Enumerate_skips_malformed_files_without_throwing()
    {
        WriteFakeSession("a3f7c102", "claude-haiku-4-5", "valid");
        File.WriteAllText(Path.Combine(_tmp, "broken.json"), "{ this is not valid json");
        File.WriteAllText(Path.Combine(_tmp, "empty.json"), "");

        var rows = SessionStore.Enumerate().ToList();

        Assert.Single(rows);
        Assert.Equal("a3f7c102", rows[0].Id);
    }

    [Fact]
    public void FindByPrefix_returns_empty_when_no_match()
    {
        WriteFakeSession("a3f7c102", "claude-haiku-4-5", null);

        var matches = SessionStore.FindByPrefix("zzz");

        Assert.Empty(matches);
    }

    [Fact]
    public void FindByPrefix_returns_single_when_unambiguous()
    {
        WriteFakeSession("a3f7c102", "claude-haiku-4-5", null);
        WriteFakeSession("b1c2d3e4", "claude-haiku-4-5", null);

        var matches = SessionStore.FindByPrefix("a3f");

        Assert.Single(matches);
        Assert.Equal("a3f7c102", matches[0].Id);
    }

    [Fact]
    public void FindByPrefix_returns_all_matches_when_ambiguous()
    {
        WriteFakeSession("a3f70000", "claude-haiku-4-5", null);
        WriteFakeSession("a3f70001", "claude-haiku-4-5", null);
        WriteFakeSession("b1c2d3e4", "claude-haiku-4-5", null);

        var matches = SessionStore.FindByPrefix("a3f");

        Assert.Equal(2, matches.Count);
        Assert.All(matches, m => Assert.StartsWith("a3f", m.Id));
    }

    [Fact]
    public void FindByPrefix_is_case_insensitive()
    {
        WriteFakeSession("a3f7c102", "claude-haiku-4-5", null);

        var matches = SessionStore.FindByPrefix("A3F");

        Assert.Single(matches);
    }

    // ---------- helpers ----------

    private void WriteFakeSession(string id, string model, string? preview)
    {
        // Mirrors the on-disk shape that SessionStore.SaveAsync writes; the
        // "session" field is opaque to the store, so for these tests we put
        // an empty object there. Real save/load tests need a fake AIAgent.
        var node = new JsonObject
        {
            ["id"]        = id,
            ["createdAt"] = DateTime.UtcNow.AddMinutes(-5),
            ["updatedAt"] = DateTime.UtcNow,
            ["model"]     = model,
            ["preview"]   = preview,
            ["session"]   = new JsonObject(),
        };
        File.WriteAllText(Path.Combine(_tmp, id + ".json"), node.ToJsonString());
    }
}
