// =============================================================================
//  TurnUsage — accumulates UsageContent across all the streaming updates of
//  one turn. A turn can span multiple RunStreamingAsync calls (approval
//  round-trips), each of which may emit its own UsageContent — we add them
//  all together so the per-turn line is honest.
//
//  Why a tiny class rather than two longs inline: it's the seam we'd extend
//  in Step 17 (budgets) to subtract from a remaining-budget counter and
//  raise when the cap is hit. Naming the concept now buys that change later.
// =============================================================================

using Microsoft.Extensions.AI;

namespace ClaudeChat.Observability;

public sealed class TurnUsage
{
    public long InputTokens  { get; private set; }
    public long OutputTokens { get; private set; }
    public long TotalTokens  => InputTokens + OutputTokens;

    public void Add(UsageDetails? details)
    {
        if (details is null) return;
        InputTokens  += details.InputTokenCount  ?? 0;
        OutputTokens += details.OutputTokenCount ?? 0;
    }

    public void Reset()
    {
        InputTokens = 0;
        OutputTokens = 0;
    }

    // "(turn: 1234 in + 567 out, $0.0123)" — or "($?)" if the model isn't
    // in our hardcoded price table. Invariant culture so the decimal point
    // stays a dot regardless of the user's locale.
    public string FormatSummary(string model)
    {
        var cost = Pricing.CostUsd(model, InputTokens, OutputTokens);
        var costStr = cost is null ? "$?" : "$" + cost.Value.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture);
        return $"(turn: {InputTokens} in + {OutputTokens} out, {costStr})";
    }
}
