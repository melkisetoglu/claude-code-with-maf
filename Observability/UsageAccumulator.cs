// =============================================================================
//  UsageAccumulator — running tally of input / output tokens (and the
//  derived cost) read from UsageContent in the stream.
//
//  Used in two scopes:
//    - Per-turn (reset every loop iteration in ChatLoop), drives the
//      "(turn: N in + M out, $X.XXXX)" line shown after each reply.
//    - Per-session (never reset, lives for the lifetime of ChatLoop.RunAsync),
//      drives the `/cost` slash command (Step 7).
//
//  Was `TurnUsage` in Step 5; renamed in Step 7 once both scopes started
//  using the same shape. The class itself didn't change; the name did,
//  because "turn" stopped being the whole story.
//
//  Step 17 (budgets) will extend this to subtract from a remaining-budget
//  counter and raise when the cap is hit.
// =============================================================================

using Microsoft.Extensions.AI;

namespace ClaudeChat.Observability;

public sealed class UsageAccumulator
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

    // Format the running totals against a per-million-token pricing table.
    // `label` lets callers say "turn", "session", or whatever; invariant
    // culture keeps the decimal point stable regardless of user locale.
    public string FormatSummary(string model, string label = "turn")
    {
        var cost = Pricing.CostUsd(model, InputTokens, OutputTokens);
        var costStr = cost is null
            ? "$?"
            : "$" + cost.Value.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture);
        return $"({label}: {InputTokens} in + {OutputTokens} out, {costStr})";
    }
}
