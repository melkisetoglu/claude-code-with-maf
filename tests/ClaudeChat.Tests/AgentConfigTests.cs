// =============================================================================
//  AgentConfigTests — JSON load round-trip, optional fields, error cases.
// =============================================================================

using ClaudeChat.Config;

namespace ClaudeChat.Tests;

[Trait("Category", "Unit")]
public sealed class AgentConfigTests : IDisposable
{
    private readonly string _tmp;

    public AgentConfigTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "claudechat-agentconfig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Loads_full_config()
    {
        var path = Path.Combine(_tmp, "agent.json");
        File.WriteAllText(path, """
        {
          "model": "claude-sonnet-4-6",
          "instructions": "You are a strict reviewer.",
          "tools": {
            "allow": ["read_file", "grep"],
            "requireApproval": []
          }
        }
        """);

        var config = AgentConfigLoader.LoadFromPath(path);

        Assert.Equal("claude-sonnet-4-6", config.Model);
        Assert.Equal("You are a strict reviewer.", config.Instructions);
        Assert.NotNull(config.Tools);
        Assert.Equal(new[] { "read_file", "grep" }, config.Tools!.Allow);
        Assert.Empty(config.Tools.RequireApproval!);
    }

    [Fact]
    public void Loads_empty_object_with_all_fields_null()
    {
        var path = Path.Combine(_tmp, "agent.json");
        File.WriteAllText(path, "{}");

        var config = AgentConfigLoader.LoadFromPath(path);

        Assert.Null(config.Model);
        Assert.Null(config.Instructions);
        Assert.Null(config.Tools);
    }

    [Fact]
    public void Loads_partial_config()
    {
        var path = Path.Combine(_tmp, "agent.json");
        File.WriteAllText(path, """{ "model": "claude-haiku-4-5" }""");

        var config = AgentConfigLoader.LoadFromPath(path);

        Assert.Equal("claude-haiku-4-5", config.Model);
        Assert.Null(config.Instructions);
        Assert.Null(config.Tools);
    }

    [Fact]
    public void Loads_tools_with_only_one_field()
    {
        var path = Path.Combine(_tmp, "agent.json");
        File.WriteAllText(path, """{ "tools": { "allow": ["read_file"] } }""");

        var config = AgentConfigLoader.LoadFromPath(path);

        Assert.NotNull(config.Tools);
        Assert.Equal(new[] { "read_file" }, config.Tools!.Allow);
        Assert.Null(config.Tools.RequireApproval);
    }

    [Fact]
    public void Tolerates_comments_and_trailing_commas()
    {
        var path = Path.Combine(_tmp, "agent.json");
        File.WriteAllText(path, """
        // top comment
        {
          "model": "claude-haiku-4-5", // trailing arg note
          "tools": { "allow": ["read_file",] },
        }
        """);

        var config = AgentConfigLoader.LoadFromPath(path);

        Assert.Equal("claude-haiku-4-5", config.Model);
    }

    [Fact]
    public void TryLoadFromCwd_returns_null_when_missing()
    {
        var cwd = Environment.CurrentDirectory;
        Environment.CurrentDirectory = _tmp;
        try
        {
            Assert.Null(AgentConfigLoader.TryLoadFromCwd());
        }
        finally
        {
            Environment.CurrentDirectory = cwd;
        }
    }

    [Fact]
    public void TryLoadFromCwd_loads_when_present()
    {
        File.WriteAllText(Path.Combine(_tmp, "agent.json"), """{ "model": "x" }""");
        var cwd = Environment.CurrentDirectory;
        Environment.CurrentDirectory = _tmp;
        try
        {
            var config = AgentConfigLoader.TryLoadFromCwd();
            Assert.NotNull(config);
            Assert.Equal("x", config!.Model);
        }
        finally
        {
            Environment.CurrentDirectory = cwd;
        }
    }

    [Fact]
    public void LoadFromPath_throws_for_missing_file()
    {
        var missing = Path.Combine(_tmp, "nope.json");
        Assert.Throws<FileNotFoundException>(() => AgentConfigLoader.LoadFromPath(missing));
    }

    [Fact]
    public void LoadFromPath_throws_for_malformed_json()
    {
        var path = Path.Combine(_tmp, "bad.json");
        File.WriteAllText(path, "{ this is not json");
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => AgentConfigLoader.LoadFromPath(path));
    }
}
