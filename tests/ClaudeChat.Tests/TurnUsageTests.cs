// =============================================================================
//  TurnUsageTests — covers token accumulation across multiple UsageDetails
//  (representing multiple RunStreamingAsync calls in one turn) and the
//  summary formatter.
// =============================================================================

using ClaudeChat.Observability;
using Microsoft.Extensions.AI;

namespace ClaudeChat.Tests;

[Trait("Category", "Unit")]
public sealed class TurnUsageTests
{
    [Fact]
    public void Empty_usage_reports_zero_tokens()
    {
        var u = new TurnUsage();

        Assert.Equal(0, u.InputTokens);
        Assert.Equal(0, u.OutputTokens);
        Assert.Equal(0, u.TotalTokens);
    }

    [Fact]
    public void Add_accumulates_input_and_output()
    {
        var u = new TurnUsage();

        u.Add(new UsageDetails { InputTokenCount = 100, OutputTokenCount = 50 });
        u.Add(new UsageDetails { InputTokenCount = 25,  OutputTokenCount = 10 });

        Assert.Equal(125, u.InputTokens);
        Assert.Equal(60,  u.OutputTokens);
        Assert.Equal(185, u.TotalTokens);
    }

    [Fact]
    public void Add_handles_null_safely()
    {
        var u = new TurnUsage();

        u.Add(null);

        Assert.Equal(0, u.TotalTokens);
    }

    [Fact]
    public void Add_treats_missing_counts_as_zero()
    {
        var u = new TurnUsage();

        u.Add(new UsageDetails { InputTokenCount = 50, OutputTokenCount = null });

        Assert.Equal(50, u.InputTokens);
        Assert.Equal(0, u.OutputTokens);
    }

    [Fact]
    public void Reset_clears_accumulator()
    {
        var u = new TurnUsage();
        u.Add(new UsageDetails { InputTokenCount = 100, OutputTokenCount = 50 });

        u.Reset();

        Assert.Equal(0, u.TotalTokens);
    }

    [Fact]
    public void FormatSummary_renders_tokens_and_cost_for_known_model()
    {
        var u = new TurnUsage();
        u.Add(new UsageDetails { InputTokenCount = 1000, OutputTokenCount = 500 });

        var summary = u.FormatSummary("claude-haiku-4-5");

        Assert.Contains("1000 in", summary);
        Assert.Contains("500 out", summary);
        Assert.Contains("$", summary);   // cost present
    }

    [Fact]
    public void FormatSummary_uses_question_mark_for_unknown_model()
    {
        var u = new TurnUsage();
        u.Add(new UsageDetails { InputTokenCount = 100, OutputTokenCount = 50 });

        var summary = u.FormatSummary("not-a-real-model");

        Assert.Contains("$?", summary);
    }
}
