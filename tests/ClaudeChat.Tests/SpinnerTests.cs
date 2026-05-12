// =============================================================================
//  SpinnerTests — covers the IAsyncDisposable shape. Animation itself is
//  hard to unit-test deterministically (timing-sensitive), so we focus on:
//    - Construction and immediate stop is a no-op (no console output when
//      stdout is redirected by the test framework).
//    - StopAsync is idempotent.
//    - DisposeAsync routes to StopAsync.
//
//  Stdout in tests is redirected (xunit captures it), so the spinner's
//  color/IsOutputRedirected guard auto-disables. That's why these tests
//  don't assert on console output.
// =============================================================================

using ClaudeChat.Harness;

namespace ClaudeChat.Tests;

[Trait("Category", "Unit")]
public sealed class SpinnerTests
{
    [Fact]
    public async Task Construct_and_stop_immediately_does_not_throw()
    {
        var s = new Spinner();
        await s.StopAsync();
    }

    [Fact]
    public async Task StopAsync_is_idempotent()
    {
        var s = new Spinner();
        await s.StopAsync();
        await s.StopAsync();
        await s.StopAsync();
    }

    [Fact]
    public async Task DisposeAsync_routes_to_StopAsync()
    {
        var s = new Spinner();
        await s.DisposeAsync();
        // Second call should also be a no-op via the idempotent path.
        await s.StopAsync();
    }
}
