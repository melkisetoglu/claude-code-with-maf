// =============================================================================
//  McpConfigTests — agent.json parsing for the Step 16 mcpServers field.
//
//  Scope: pin the JSON schema only. Actual MCP connection (HTTP transport,
//  tools/list) needs a live MCP server and is covered by smoke tests, not
//  unit tests — bringing up Playwright MCP from a test runner is ceremony
//  out of proportion to the coverage it'd add. The chapter calls this out.
// =============================================================================

using ClaudeChat.Config;

namespace ClaudeChat.Tests;

[Trait("Category", "Unit")]
public sealed class McpConfigTests : IDisposable
{
    private readonly string _tmp;

    public McpConfigTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "claudechat-mcp-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best-effort */ }
    }

    private string WriteConfig(string json)
    {
        var path = Path.Combine(_tmp, "agent.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Empty_config_parses_with_null_mcpServers()
    {
        var path = WriteConfig("{}");

        var config = AgentConfigLoader.LoadFromPath(path);

        Assert.Null(config.McpServers);
    }

    [Fact]
    public void Single_minimal_server_parses()
    {
        var path = WriteConfig("""
        {
          "mcpServers": [
            {
              "name": "playwright",
              "address": "http://localhost:8931/mcp"
            }
          ]
        }
        """);

        var config = AgentConfigLoader.LoadFromPath(path);

        Assert.NotNull(config.McpServers);
        Assert.Single(config.McpServers!);
        var s = config.McpServers![0];
        Assert.Equal("playwright", s.Name);
        Assert.Equal("http://localhost:8931/mcp", s.Address);
        Assert.Null(s.Description);
        Assert.Null(s.ApprovalMode);    // defaults to "always" at use-time
        Assert.Null(s.AllowedTools);
        Assert.Null(s.Headers);
    }

    [Fact]
    public void Full_server_entry_parses_every_field()
    {
        var path = WriteConfig("""
        {
          "mcpServers": [
            {
              "name": "playwright",
              "address": "http://localhost:8931/mcp",
              "description": "Local browser automation",
              "approvalMode": "never",
              "allowedTools": ["browser_navigate", "browser_take_screenshot"],
              "headers": {
                "Authorization": "Bearer xyz",
                "X-Custom": "value"
              }
            }
          ]
        }
        """);

        var config = AgentConfigLoader.LoadFromPath(path);

        var s = config.McpServers![0];
        Assert.Equal("playwright", s.Name);
        Assert.Equal("Local browser automation", s.Description);
        Assert.Equal("never", s.ApprovalMode);
        Assert.NotNull(s.AllowedTools);
        Assert.Contains("browser_navigate", s.AllowedTools!);
        Assert.Contains("browser_take_screenshot", s.AllowedTools!);
        Assert.NotNull(s.Headers);
        Assert.Equal("Bearer xyz", s.Headers!["Authorization"]);
        Assert.Equal("value", s.Headers["X-Custom"]);
    }

    [Fact]
    public void Multiple_servers_parse_in_order()
    {
        var path = WriteConfig("""
        {
          "mcpServers": [
            { "name": "playwright", "address": "http://localhost:8931/mcp" },
            { "name": "filesystem", "address": "http://localhost:9000/mcp" }
          ]
        }
        """);

        var config = AgentConfigLoader.LoadFromPath(path);

        Assert.Equal(2, config.McpServers!.Count);
        Assert.Equal("playwright", config.McpServers[0].Name);
        Assert.Equal("filesystem", config.McpServers[1].Name);
    }
}
