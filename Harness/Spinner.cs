// =============================================================================
//  Spinner — animated ⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏ glyph that runs while waiting for the
//  model's first content chunk.
//
//  Lifecycle (per RunStreamingAsync round-trip):
//    var spinner = new Spinner();          // starts a background task
//    await foreach (var update in ...) {
//      foreach (var content in update.Contents) {
//          await spinner.StopAsync();      // first content -> stop animation
//          // ...write content...
//      }
//    }
//    await spinner.StopAsync();            // idempotent
//
//  Thread safety: the background task writes a glyph + sleeps + erases it.
//  StopAsync signals cancellation and awaits the loop's exit, so by the time
//  it returns we're guaranteed no more writes from the spinner. The main
//  task can then write content without racing.
//
//  Output guard: if stdout is redirected (piped, smoke tests), the spinner
//  is a no-op. Otherwise it would pollute captured output with glyphs +
//  backspaces.
// =============================================================================

namespace ClaudeChat.Harness;

public sealed class Spinner : IAsyncDisposable
{
    private static readonly char[] Frames = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏".ToCharArray();
    private const int FrameMs = 80;

    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly bool _enabled;
    private int _disposed;

    public Spinner()
    {
        _enabled = !Console.IsOutputRedirected;
        _loop = _enabled ? Task.Run(RunAsync) : Task.CompletedTask;
    }

    public async ValueTask StopAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        if (!_enabled) return;

        _stop.Cancel();
        try { await _loop.ConfigureAwait(false); } catch { /* loop exit is best-effort */ }
    }

    public ValueTask DisposeAsync() => StopAsync();

    private async Task RunAsync()
    {
        int i = 0;
        bool drawn = false;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                Console.Write(Frames[i % Frames.Length]);
                drawn = true;
                try
                {
                    await Task.Delay(FrameMs, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { /* fall through to erase */ }

                Console.Write("\b \b");   // erase glyph + reset cursor
                drawn = false;
                i++;
            }
        }
        finally
        {
            // Ensure no glyph is left visible if the loop exited at an odd
            // point (e.g. between Write and Delay).
            if (drawn)
            {
                try { Console.Write("\b \b"); } catch { /* console gone */ }
            }
        }
    }
}
