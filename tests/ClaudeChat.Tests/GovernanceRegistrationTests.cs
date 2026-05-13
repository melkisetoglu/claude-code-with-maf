// =============================================================================
//  GovernanceRegistrationTests — pin the constants AgentBuilder uses for
//  Microsoft.AgentGovernance integration so a refactor can't silently
//  break the convention.
//
//  We don't construct a real GovernanceKernel here because that loads a
//  YAML policy file. The Quickstart-style integration is verified by
//  smoke tests (live agent runs with policies/default.yaml present;
//  /governance shows audit events; policies enforce rate limits).
// =============================================================================

using ClaudeChat.Agent;

namespace ClaudeChat.Tests;

[Trait("Category", "Unit")]
public sealed class GovernanceRegistrationTests
{
    [Fact]
    public void Constants_pin_the_policy_directory_and_filename()
    {
        // /governance and AgentBuilder both reference these.
        Assert.Equal("policies", AgentBuilder.PoliciesDirectoryName);
        Assert.Equal("default.yaml", AgentBuilder.DefaultPolicyFileName);
    }

    [Fact]
    public void Constant_pins_the_agent_id_used_in_audit_events()
    {
        // Audit events record the agent identifier; changing this would
        // break consumers parsing the audit trail (e.g., the workshop's
        // /governance slash command, plus any external SIEM ingest).
        Assert.Equal("did:claudechat:main", AgentBuilder.GovernanceAgentId);
    }
}
