// =============================================================================
//  GovernanceCommandTests — the /governance slash command (Step 17).
//
//  Two paths:
//    - Not attached (ctx.Governance == null) — prints the opt-in hint
//      pointing at ./policies/default.yaml.
//    - Attached but empty audit trail — prints kernel state + "no events yet".
//
//  Full audit-event rendering (with real GovernanceEvent instances) is
//  covered by live smoke; the slash command's job is mostly to print
//  ctx.AuditTrail entries via ToString(), which is AGT's territory.
// =============================================================================

using ClaudeChat.Agent;
using ClaudeChat.Harness.Commands;
using ClaudeChat.Observability;

namespace ClaudeChat.Tests;

[Trait("Category", "Unit")]
[Collection("Console-shared-static")]
public sealed class GovernanceCommandTests : IDisposable
{
    private readonly TextWriter _previousOut;
    private readonly StringWriter _capturedOut = new();

    public GovernanceCommandTests()
    {
        _previousOut = Console.Out;
        Console.SetOut(_capturedOut);
    }

    public void Dispose()
    {
        Console.SetOut(_previousOut);
    }

    private static SlashContext MakeContext(bool attached) =>
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
            // attached=true would require a real GovernanceKernel which
            // loads a policy file — defer that path to live smoke.
            Governance = null,
            AuditTrail = Array.Empty<AgentGovernance.Audit.GovernanceEvent>(),
        };

    [Fact]
    public void No_policy_file_prints_opt_in_hint()
    {
        var registry = SlashRegistry.Default();

        var action = registry.TryDispatch("/governance", MakeContext(attached: false));

        Assert.Equal(SlashAction.Continue, action);
        var output = _capturedOut.ToString();
        Assert.Contains(AgentBuilder.PoliciesDirectoryName, output);
        Assert.Contains(AgentBuilder.DefaultPolicyFileName, output);
        Assert.Contains("not attached", output);
        Assert.Contains("tutorial/17-governance.md", output);
    }

    [Fact]
    public void Command_is_registered_in_default_registry()
    {
        var registry = SlashRegistry.Default();
        var names = registry.All.Select(c => c.Name).ToList();

        Assert.Contains("/governance", names);
    }
}
