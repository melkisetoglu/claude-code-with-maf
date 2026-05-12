// =============================================================================
//  CompactionRegistrationTests — pin the constants and the recipe for
//  building the CompactionProvider that AgentBuilder uses.
//
//  We don't exercise the full Build() path because it constructs a real
//  AnthropicClient and would need an API key. Instead these tests:
//    1. Pin the per-model window constants so a stray "fix" can't silently
//       change them.
//    2. Construct a CompactionProvider the exact same way AgentBuilder does,
//       and verify it reports our state key — i.e. the wiring recipe is
//       internally consistent.
//
//  Real-world coverage for "compaction state persists across resume" comes
//  from the smoke tests, where the session JSON has been shown to contain
//  the "ClaudeChat.Compaction" stateBag entry.
// =============================================================================

using ClaudeChat.Agent;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeChat.Tests;

[Trait("Category", "Unit")]
public sealed class CompactionRegistrationTests
{
    [Fact]
    public void Constants_have_sane_values()
    {
        // Step 10 hardcodes Claude Haiku 4.5 / Sonnet 4.6 window sizes.
        // If we ever bump model class, update these and the chapter.
        Assert.Equal(200_000, AgentBuilder.CompactionContextWindowTokens);
        Assert.Equal(8_000,   AgentBuilder.CompactionMaxOutputTokens);
        Assert.False(string.IsNullOrEmpty(AgentBuilder.CompactionStateKey));
        Assert.Equal("ClaudeChat.Compaction", AgentBuilder.CompactionStateKey);
    }

    [Fact]
    public void Provider_built_with_our_recipe_reports_our_state_key()
    {
#pragma warning disable MAAI001
        // Mirror AgentBuilder's exact construction recipe.
        var provider = new CompactionProvider(
            new ContextWindowCompactionStrategy(
                maxContextWindowTokens: AgentBuilder.CompactionContextWindowTokens,
                maxOutputTokens:        AgentBuilder.CompactionMaxOutputTokens),
            stateKey: AgentBuilder.CompactionStateKey,
            loggerFactory: NullLoggerFactory.Instance);
#pragma warning restore MAAI001

        Assert.NotNull(provider);
        Assert.Contains(AgentBuilder.CompactionStateKey, provider.StateKeys);
    }
}
