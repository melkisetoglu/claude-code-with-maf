// =============================================================================
//  AgentsCommandTests — the /agents slash command (Step 15).
//
//  Simplest of the command tests so far: /agents prints static
//  configuration (researcher name, tools, model — all known at
//  AgentBuilder time). It doesn't touch cwd, doesn't touch the provider,
//  doesn't touch Session. Just describes what was wired.
// =============================================================================

using ClaudeChat.Agent;
using ClaudeChat.Harness.Commands;
using ClaudeChat.Observability;

namespace ClaudeChat.Tests;

[Trait("Category", "Unit")]
[Collection("Console-shared-static")]
public sealed class AgentsCommandTests : IDisposable
{
    private readonly TextWriter _previousOut;
    private readonly StringWriter _capturedOut = new();

    public AgentsCommandTests()
    {
        _previousOut = Console.Out;
        Console.SetOut(_capturedOut);
    }

    public void Dispose()
    {
        Console.SetOut(_previousOut);
    }

    private static SlashContext MakeContext() =>
        new()
        {
            SessionId = "abc12345",
            Session = null!,
            CreatedAt = DateTime.UtcNow,
            Preview = null,
            Agent = null!,
            Model = "claude-haiku-4-5",
            Config = null,
            SessionUsage = new UsageAccumulator(),
            Approval = new ApprovalState(),
        };

    [Fact]
    public void Command_is_dispatchable_and_lists_researcher()
    {
        var registry = SlashRegistry.Default();

        var action = registry.TryDispatch("/agents", MakeContext());

        Assert.Equal(SlashAction.Continue, action);
        var output = _capturedOut.ToString();
        Assert.Contains(AgentBuilder.ResearcherAgentName, output);
        Assert.Contains("read-only", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Output_lists_the_researcher_tool_set()
    {
        var registry = SlashRegistry.Default();

        registry.TryDispatch("/agents", MakeContext());

        var output = _capturedOut.ToString();
        // The researcher's tool set should be explicit so the user knows
        // what they're delegating to.
        Assert.Contains("read_file", output);
        Assert.Contains("list_dir",  output);
        Assert.Contains("glob",      output);
        Assert.Contains("grep",      output);
        // Mutation tools should NOT be listed (the researcher doesn't have them).
        Assert.DoesNotContain("write_file", output);
        Assert.DoesNotContain("edit_file",  output);
        Assert.DoesNotContain("bash",       output);
    }

    [Fact]
    public void Output_explains_how_the_model_delegates()
    {
        // The /agents output is what users see when they want to know
        // what sub-agents are available + how the main agent reaches them.
        // Pin the delegation-tool name so a user reading /agents knows the
        // entry point.
        var registry = SlashRegistry.Default();

        registry.TryDispatch("/agents", MakeContext());

        var output = _capturedOut.ToString();
        Assert.Contains("SubAgents_StartTask", output);
    }
}
