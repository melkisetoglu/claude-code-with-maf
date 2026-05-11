// =============================================================================
//  PricingTests — covers the cost math + lookup behaviour.
// =============================================================================

using ClaudeChat.Observability;

namespace ClaudeChat.Tests;

[Trait("Category", "Unit")]
public sealed class PricingTests
{
    [Fact]
    public void Lookup_returns_rates_for_known_model()
    {
        var rates = Pricing.Lookup("claude-haiku-4-5");

        Assert.NotNull(rates);
        Assert.Equal(1.00m, rates.Value.InputPerMTok);
        Assert.Equal(5.00m, rates.Value.OutputPerMTok);
    }

    [Fact]
    public void Lookup_is_case_insensitive()
    {
        var rates = Pricing.Lookup("CLAUDE-HAIKU-4-5");

        Assert.NotNull(rates);
    }

    [Fact]
    public void Lookup_returns_null_for_unknown_model()
    {
        var rates = Pricing.Lookup("not-a-real-model");

        Assert.Null(rates);
    }

    [Fact]
    public void Cost_for_haiku_per_million_input_is_one_dollar()
    {
        // 1,000,000 input tokens × $1.00/MTok input + 0 output = $1.00 exactly.
        var cost = Pricing.CostUsd("claude-haiku-4-5", 1_000_000, 0);

        Assert.Equal(1.00m, cost);
    }

    [Fact]
    public void Cost_combines_input_and_output()
    {
        // haiku: 500K input @ $1/MTok = $0.50; 100K output @ $5/MTok = $0.50; total $1.00.
        var cost = Pricing.CostUsd("claude-haiku-4-5", 500_000, 100_000);

        Assert.Equal(1.00m, cost);
    }

    [Fact]
    public void Cost_for_sonnet_uses_sonnet_rates()
    {
        // sonnet: 1M input @ $3/MTok = $3.00.
        var cost = Pricing.CostUsd("claude-sonnet-4-6", 1_000_000, 0);

        Assert.Equal(3.00m, cost);
    }

    [Fact]
    public void Cost_returns_null_for_unknown_model()
    {
        var cost = Pricing.CostUsd("not-a-real-model", 1000, 100);

        Assert.Null(cost);
    }

    [Fact]
    public void Cost_for_small_input_yields_fractional_cents()
    {
        // 1000 input tokens @ $1/MTok = $0.001
        var cost = Pricing.CostUsd("claude-haiku-4-5", 1000, 0);

        Assert.Equal(0.001m, cost);
    }
}
