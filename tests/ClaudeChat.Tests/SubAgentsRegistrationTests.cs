// =============================================================================
//  SubAgentsRegistrationTests — pin the recipe AgentBuilder uses to configure
//  the SubAgentsProvider so a refactor can't silently change what's wired.
//
//  Scope note: SubAgentsProvider's ctor takes IEnumerable<AIAgent> and the
//  framework REJECTS an empty list with ArgumentException at construction
//  time. To exercise the recipe in a unit test we need at least one real
//  AIAgent — we use the AnthropicClient.AsAIAgent path with a placeholder
//  API key. Construction is sync and doesn't hit the network (verified
//  empirically); a real model call would, but the tests below don't make
//  any. If a future MAF preview changes that and AsAIAgent starts probing
//  the network at construction, the tests will surface it loudly.
//
//  Tests cover:
//    - the researcher-name constant pins to "researcher";
//    - SubAgentsProvider can be constructed with one stub sub-agent
//      (pins the positional ctor: agents, options);
//    - StateKeys contains a "SubAgentsProvider"-shaped key (the framework
//      persists task state in the session bag under that key, verified
//      live during Step 15).
//
//  Coverage for "the researcher actually runs delegated tasks" comes from
//  live smoke. AT TIME OF WRITING that smoke FAILS due to MAF preview
//  drift (WebSearchToolResultContent.Results rename); the chapter
//  documents this openly. The tests below pass because they don't
//  exercise the broken execution path.
// =============================================================================

using Anthropic;
using ClaudeChat.Agent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace ClaudeChat.Tests;

[Trait("Category", "Unit")]
public sealed class SubAgentsRegistrationTests
{
    private static AIAgent BuildStubAgent()
    {
        // AnthropicClient construction is sync + offline; AsAIAgent just
        // wraps the client. No network calls happen here.
        var client = new AnthropicClient { ApiKey = "fake-key-for-tests" };
        return client.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "stub",
            ChatOptions = new ChatOptions
            {
                ModelId      = "claude-haiku-4-5",
                Instructions = "stub",
            },
        });
    }

    [Fact]
    public void Constant_pins_the_researcher_name()
    {
        // The /agents slash command and AgentBuilder's wiring both reference
        // this. Changing it would split that contract.
        Assert.Equal("researcher", AgentBuilder.ResearcherAgentName);
    }

    [Fact]
    public void Provider_built_with_our_recipe_is_an_AIContextProvider()
    {
        var stub = BuildStubAgent();
#pragma warning disable MAAI001
        var provider = new SubAgentsProvider(
            new[] { stub },
            new SubAgentsProviderOptions { Instructions = "test" });
#pragma warning restore MAAI001

        Assert.NotNull(provider);
        AIContextProvider asBase = provider;
        Assert.NotNull(asBase);
    }

    [Fact]
    public void Provider_reports_a_SubAgentsProvider_state_key()
    {
        var stub = BuildStubAgent();
#pragma warning disable MAAI001
        var provider = new SubAgentsProvider(
            new[] { stub },
            new SubAgentsProviderOptions { Instructions = "test" });
#pragma warning restore MAAI001

        // The framework persists task state in the session bag under
        // "SubAgentsProvider" + "SubAgentsProvider_Runtime" (verified live
        // — the saved JSON's stateBag had both keys). Pin some-such-key.
        Assert.NotEmpty(provider.StateKeys);
        Assert.Contains(provider.StateKeys, key =>
            key.Contains("SubAgent", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Empty_agent_list_is_rejected_by_the_framework()
    {
        // Pin the framework's empty-list rejection — workshop semantics
        // assume the provider only attaches when we have at least one
        // sub-agent, and this captures the contract.
#pragma warning disable MAAI001
        var ex = Assert.Throws<ArgumentException>(() =>
            new SubAgentsProvider(
                Array.Empty<AIAgent>(),
                new SubAgentsProviderOptions { Instructions = "test" }));
#pragma warning restore MAAI001

        Assert.Contains("sub-agent", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
