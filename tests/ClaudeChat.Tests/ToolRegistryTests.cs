// =============================================================================
//  ToolRegistryTests — covers AgentBuilder.ResolveTools, which is the data
//  layer behind agent.json's tools.allow / tools.requireApproval semantics.
//
//  Doesn't construct a full AIAgent — we just resolve the tools list and
//  assert its shape. ApprovalRequiredAIFunction is the marker the gate
//  reads later; we check for its presence by type.
// =============================================================================

using ClaudeChat.Agent;
using ClaudeChat.Config;
using Microsoft.Extensions.AI;

namespace ClaudeChat.Tests;

[Trait("Category", "Unit")]
public sealed class ToolRegistryTests
{
    [Fact]
    public void Null_tools_config_returns_all_known_tools_with_default_approval()
    {
        var tools = AgentBuilder.ResolveTools(null);

        // All seven known tools registered.
        Assert.Equal(AgentBuilder.ToolRegistry.Count, tools.Count);

        var names = tools.Select(t => ((AIFunction)t).Name).OrderBy(n => n).ToArray();
        var expected = AgentBuilder.ToolRegistry.Keys.OrderBy(n => n).ToArray();
        Assert.Equal(expected, names);

        // Default require-approval set: write_file / edit_file / bash wrapped,
        // read_file / list_dir / glob / grep not wrapped.
        Assert.IsType<ApprovalRequiredAIFunction>(FindByName(tools, "write_file"));
        Assert.IsType<ApprovalRequiredAIFunction>(FindByName(tools, "edit_file"));
        Assert.IsType<ApprovalRequiredAIFunction>(FindByName(tools, "bash"));
        Assert.IsNotType<ApprovalRequiredAIFunction>(FindByName(tools, "read_file"));
        Assert.IsNotType<ApprovalRequiredAIFunction>(FindByName(tools, "list_dir"));
    }

    [Fact]
    public void Empty_allow_yields_no_tools()
    {
        var tools = AgentBuilder.ResolveTools(new ToolsConfig(Allow: new List<string>(), RequireApproval: null));

        Assert.Empty(tools);
    }

    [Fact]
    public void Allow_subset_registers_only_those_tools()
    {
        var tools = AgentBuilder.ResolveTools(
            new ToolsConfig(Allow: new[] { "read_file", "grep" }, RequireApproval: null));

        Assert.Equal(2, tools.Count);
        Assert.NotNull(FindByName(tools, "read_file"));
        Assert.NotNull(FindByName(tools, "grep"));
    }

    [Fact]
    public void RequireApproval_overrides_default_set()
    {
        // Demand approval on read_file (unusual but valid), no approval on
        // write_file (even though it's normally gated).
        var tools = AgentBuilder.ResolveTools(
            new ToolsConfig(
                Allow: new[] { "read_file", "write_file" },
                RequireApproval: new[] { "read_file" }));

        Assert.IsType<ApprovalRequiredAIFunction>(FindByName(tools, "read_file"));
        Assert.IsNotType<ApprovalRequiredAIFunction>(FindByName(tools, "write_file"));
    }

    [Fact]
    public void Empty_requireApproval_disables_all_gating()
    {
        var tools = AgentBuilder.ResolveTools(
            new ToolsConfig(Allow: new[] { "write_file", "bash" }, RequireApproval: new List<string>()));

        Assert.IsNotType<ApprovalRequiredAIFunction>(FindByName(tools, "write_file"));
        Assert.IsNotType<ApprovalRequiredAIFunction>(FindByName(tools, "bash"));
    }

    [Fact]
    public void Unknown_tool_in_allow_throws_with_known_list_in_message()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AgentBuilder.ResolveTools(new ToolsConfig(Allow: new[] { "read_file", "foobar" }, RequireApproval: null)));

        Assert.Contains("foobar", ex.Message);
        Assert.Contains("known tools", ex.Message);
        Assert.Contains("read_file", ex.Message);   // known-list dump includes real names
    }

    [Fact]
    public void RequireApproval_not_in_allow_throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AgentBuilder.ResolveTools(new ToolsConfig(
                Allow: new[] { "read_file" },
                RequireApproval: new[] { "write_file" })));

        Assert.Contains("write_file", ex.Message);
        Assert.Contains("subset", ex.Message);
    }

    // ApprovalRequiredAIFunction is itself an AIFunction (via DelegatingAIFunction),
    // and its .Name forwards to the wrapped inner function. Single check works
    // for both wrapped and unwrapped entries.
    private static AITool FindByName(List<AITool> tools, string name) =>
        tools.First(t => t is AIFunction f && f.Name == name);
}
