// =============================================================================
//  Pricing — hardcoded $/million-token rates for the two models the workshop
//  supports. Refresh these when pricing changes upstream.
//
//  WORKSHOP SCOPE: a production deployment would load these from a config
//  file or fetch live. We hardcode so the math is visible and stable for
//  the workshop's purposes; the chapter spells this out explicitly.
//
//  Reference (as of Anthropic's published pricing for the listed models):
//    claude-haiku-4-5    $1.00 / MTok input, $5.00 / MTok output
//    claude-sonnet-4-6   $3.00 / MTok input, $15.00 / MTok output
//
//  Unknown models fall back to a zero-cost row so the per-turn line shows
//  "$?" instead of crashing.
// =============================================================================

namespace ClaudeChat.Observability;

public readonly record struct Rates(decimal InputPerMTok, decimal OutputPerMTok);

public static class Pricing
{
    private static readonly Dictionary<string, Rates> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        // Source: Anthropic pricing, refresh as needed.
        ["claude-haiku-4-5"]  = new Rates(1.00m, 5.00m),
        ["claude-sonnet-4-6"] = new Rates(3.00m, 15.00m),
    };

    public static Rates? Lookup(string model) =>
        Table.TryGetValue(model, out var rates) ? rates : null;

    public static decimal? CostUsd(string model, long inputTokens, long outputTokens)
    {
        var rates = Lookup(model);
        if (rates is null) return null;
        return inputTokens  / 1_000_000m * rates.Value.InputPerMTok
             + outputTokens / 1_000_000m * rates.Value.OutputPerMTok;
    }
}
