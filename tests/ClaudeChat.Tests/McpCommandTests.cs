// =============================================================================
//  McpCommandTests — the /mcp slash command (Step 16).
//
//  Reads ctx.Config.McpServers and prints it. Pure formatting; no live
//  connection needed. Tests cover:
//    - empty / null config prints the opt-in hint;
//    - configured server is listed with its fields, headers redacted.
// =============================================================================

using ClaudeChat.Agent;
using ClaudeChat.Config;
using ClaudeChat.Harness.Commands;
using ClaudeChat.Observability;

namespace ClaudeChat.Tests;

[Trait("Category", "Unit")]
[Collection("Console-shared-static")]
public sealed class McpCommandTests : IDisposable
{
    private readonly TextWriter _previousOut;
    private readonly StringWriter _capturedOut = new();

    public McpCommandTests()
    {
        _previousOut = Console.Out;
        Console.SetOut(_capturedOut);
    }

    public void Dispose()
    {
        Console.SetOut(_previousOut);
    }

    private static SlashContext MakeContext(AgentConfig? config = null) =>
        new()
        {
            SessionId = "abc12345",
            Session = null!,
            CreatedAt = DateTime.UtcNow,
            Preview = null,
            Agent = null!,
            Model = "claude-haiku-4-5",
            Config = config,
            SessionUsage = new UsageAccumulator(),
            Approval = new ApprovalState(),
        };

    [Fact]
    public void No_config_prints_opt_in_hint()
    {
        var registry = SlashRegistry.Default();

        var action = registry.TryDispatch("/mcp", MakeContext(config: null));

        Assert.Equal(SlashAction.Continue, action);
        var output = _capturedOut.ToString();
        Assert.Contains("no MCP servers configured", output);
        Assert.Contains("agent.json", output);
        Assert.Contains("tutorial/16-mcp.md", output);
    }

    [Fact]
    public void Empty_mcpServers_list_treated_as_no_config()
    {
        var config = new AgentConfig(
            Model: null,
            Instructions: null,
            Tools: null,
            McpServers: new List<McpServerConfig>());

        var registry = SlashRegistry.Default();
        registry.TryDispatch("/mcp", MakeContext(config));

        Assert.Contains("no MCP servers configured", _capturedOut.ToString());
    }

    [Fact]
    public void Configured_server_is_listed_with_fields()
    {
        var config = new AgentConfig(
            Model: null,
            Instructions: null,
            Tools: null,
            McpServers: new[]
            {
                new McpServerConfig(
                    Name: "playwright",
                    Address: "http://localhost:8931/mcp",
                    Description: "Local browser automation",
                    ApprovalMode: "always",
                    AllowedTools: null,
                    Headers: null),
            });

        var registry = SlashRegistry.Default();
        registry.TryDispatch("/mcp", MakeContext(config));

        var output = _capturedOut.ToString();
        Assert.Contains("playwright", output);
        Assert.Contains("http://localhost:8931/mcp", output);
        Assert.Contains("Local browser automation", output);
        Assert.Contains("always", output);
    }

    [Fact]
    public void Headers_are_redacted_in_output()
    {
        var config = new AgentConfig(
            Model: null,
            Instructions: null,
            Tools: null,
            McpServers: new[]
            {
                new McpServerConfig(
                    Name: "secret-server",
                    Address: "https://example.com/mcp",
                    Description: null,
                    ApprovalMode: null,
                    AllowedTools: null,
                    Headers: new Dictionary<string, string>
                    {
                        ["Authorization"] = "Bearer SECRET_TOKEN_12345",
                    }),
            });

        var registry = SlashRegistry.Default();
        registry.TryDispatch("/mcp", MakeContext(config));

        var output = _capturedOut.ToString();
        // Header KEY is shown; VALUE is NOT.
        Assert.Contains("Authorization", output);
        Assert.DoesNotContain("Bearer SECRET_TOKEN_12345", output);
        Assert.DoesNotContain("SECRET_TOKEN", output);
    }

    [Fact]
    public void Allowed_tools_listed_when_present()
    {
        var config = new AgentConfig(
            Model: null,
            Instructions: null,
            Tools: null,
            McpServers: new[]
            {
                new McpServerConfig(
                    Name: "playwright",
                    Address: "http://localhost:8931/mcp",
                    Description: null,
                    ApprovalMode: null,
                    AllowedTools: new[] { "browser_navigate", "browser_take_screenshot" },
                    Headers: null),
            });

        var registry = SlashRegistry.Default();
        registry.TryDispatch("/mcp", MakeContext(config));

        var output = _capturedOut.ToString();
        Assert.Contains("browser_navigate", output);
        Assert.Contains("browser_take_screenshot", output);
    }
}
