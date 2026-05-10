// =============================================================================
//  ChatLoop — the interactive chat loop and harness-level command dispatch.
//
//  This is the "Claude Code feel" layer, not MAF. The loop:
//    1. Reads a user line.
//    2. Handles harness-level commands (/exit, /quit, /clear, /id) without
//       round-tripping to the model.
//    3. Streams a turn via RunStreamingAsync.
//    4. Persists the session to disk.
//
//  Persisting per turn (rather than only on exit) means Ctrl+C can't lose state.
//
//  Future steps that touch this file:
//    - Step 07: replace the inline if-chain with a real /command dispatcher
//               (/help, /tools, /cost, /model, /sessions, …).
//    - Step 08: plan mode toggle.
//    - Step 09: Ctrl+C interrupt of an in-flight stream + spinner.
// =============================================================================

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ClaudeChat.Persistence;

namespace ClaudeChat.Harness;

public static class ChatLoop
{
    public static async Task RunAsync(
        AIAgent agent,
        string model,
        string sessionId,
        AgentSession session,
        DateTime createdAt,
        string? preview)
    {
        Console.WriteLine($"Model: {model}. Commands: '/exit' (quit), '/clear' (new session), '/id' (show id).\n");

        while (true)
        {
            Console.Write("you > ");
            var input = Console.ReadLine();
            if (input is null) break;                                          // Ctrl+D
            if (input.Equals("/exit", StringComparison.OrdinalIgnoreCase)) break;
            if (input.Equals("/quit", StringComparison.OrdinalIgnoreCase)) break;
            if (string.IsNullOrWhiteSpace(input)) continue;

            if (input.Equals("/id", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"(session: {sessionId})\n");
                continue;
            }

            if (input.Equals("/clear", StringComparison.OrdinalIgnoreCase))
            {
                // Mint a new session in-place. The previous one stays on disk —
                // unlike `rm session.json`, this is non-destructive.
                sessionId = SessionStore.NewId();
                session = await agent.CreateSessionAsync();
                createdAt = DateTime.UtcNow;
                preview = null;
                Console.WriteLine($"(new session: {sessionId})\n");
                continue;
            }

            // Streaming turn. RunStreamingAsync returns IAsyncEnumerable<AgentResponseUpdate>.
            // Each update has a Contents list of AIContent items: TextContent
            // for streamed text, FunctionCallContent when the model decides
            // to call a tool, FunctionResultContent for the tool's return.
            // We render text inline and announce tool calls on their own line
            // so the agentic loop is visible.
            Console.Write("claude > ");
            await foreach (var update in agent.RunStreamingAsync(input, session))
            {
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case TextContent text:
                            Console.Write(text.Text);
                            break;
                        case FunctionCallContent call:
                            // Render every argument as key="value", truncating
                            // long values so a multi-line regex doesn't dump
                            // into the terminal.
                            Console.WriteLine($"\n[{FormatCall(call)}]");
                            break;
                    }
                }
            }
            Console.WriteLine("\n");

            // Lazily set a preview the first time we have user input — this is what
            // shows up in `--list` so you can recognize the session.
            preview ??= input.Length > 60 ? input[..60] + "..." : input;
            await SessionStore.SaveAsync(sessionId, createdAt, model, preview, session, agent);
        }

        Console.WriteLine($"\n(session saved: {sessionId} — resume with: dotnet run -- --resume {sessionId})");
    }

    // Render a tool-call as "name: key=\"val\", key=\"val\"" — truncates each
    // value so a long regex or multi-line argument doesn't blow up a line.
    private static string FormatCall(FunctionCallContent call)
    {
        if (call.Arguments is null || call.Arguments.Count == 0)
            return call.Name;

        var args = string.Join(", ",
            call.Arguments.Select(kv => $"{kv.Key}=\"{Truncate(kv.Value?.ToString() ?? "")}\""));
        return $"{call.Name}: {args}";
    }

    private static string Truncate(string s, int max = 60) =>
        s.Length <= max ? s : s[..max] + "...";
}
