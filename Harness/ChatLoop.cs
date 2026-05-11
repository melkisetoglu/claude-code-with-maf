// =============================================================================
//  ChatLoop — the interactive chat loop and harness-level command dispatch.
//
//  This is the "Claude Code feel" layer, not MAF. The loop:
//    1. Reads a user line.
//    2. If it starts with '/', hand it to the SlashRegistry (Step 7). The
//       registry handles /help, /clear, /id, /exit, /tools, /cost, /model,
//       /sessions, /yolo without round-tripping to the model.
//    3. Otherwise: streams a turn via RunStreamingAsync, possibly looping
//       if the model hits an approval-required tool (Step 3+): we collect
//       any ToolApprovalRequestContent emitted in the stream, prompt the
//       user, and resume the same conversation by feeding the responses
//       back in a new RunStreamingAsync call.
//    4. Persists the session to disk.
//
//  Persisting per turn (rather than only on exit) means Ctrl+C can't lose
//  state.
//
//  Future steps that touch this file:
//    - Step 08: plan mode toggle (likely a /plan + /accept-plan command).
//    - Step 09: Ctrl+C interrupt of an in-flight stream + spinner.
// =============================================================================

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ClaudeChat.Config;
using ClaudeChat.Harness.Commands;
using ClaudeChat.Observability;
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
        string? preview,
        AgentConfig? agentConfig)
    {
        // Step 7: registry + state objects that the slash commands and the
        // approval prompt share.
        var registry = SlashRegistry.Default();
        var approval = new ApprovalState();
        var sessionUsage = new UsageAccumulator();
        var ctx = new SlashContext
        {
            SessionId    = sessionId,
            Session      = session,
            CreatedAt    = createdAt,
            Preview      = preview,
            Agent        = agent,
            Model        = model,
            Config       = agentConfig,
            SessionUsage = sessionUsage,
            Approval     = approval,
        };

        Console.WriteLine($"Model: {model}. Type /help for commands.\n");

        while (true)
        {
            // Prompt label reflects current mode so the state is visible
            // every turn rather than only when /plan or /yolo print.
            Console.Write(PromptLabel(approval));
            var input = Console.ReadLine();
            if (input is null) break;                                          // Ctrl+D
            if (string.IsNullOrWhiteSpace(input)) continue;

            // Step 7 dispatcher. Returns null if the input isn't a slash
            // command; in that case it's a chat turn.
            var slashResult = registry.TryDispatch(input.TrimEnd(), ctx);
            if (slashResult == SlashAction.Exit) break;
            if (slashResult == SlashAction.Continue) continue;

            // Step 8: in plan mode, prefix every chat turn with a reminder
            // so the model doesn't drift over many turns. The system prompt
            // is set once at agent construction; this lives in the user
            // message stream so it's still in context when history grows.
            var effectiveInput = approval.PlanMode
                ? "[plan mode: read-only — produce a plan, do not modify or run anything] " + input
                : input;

            // A single user input may require multiple RunStreamingAsync
            // calls if the model hits an approval-required tool: stream
            // emits ToolApprovalRequestContent → we prompt → we feed the
            // ToolApprovalResponseContent back as a follow-up message →
            // stream resumes. Each iteration of the inner loop is one such
            // round-trip. UsageContent across iterations is folded into
            // both the per-turn accumulator and the running session total.
            Console.Write("claude > ");
            ChatMessage nextMessage = new(ChatRole.User, effectiveInput);
            var turnUsage = new UsageAccumulator();
            while (true)
            {
                var pendingApprovals = new List<ToolApprovalRequestContent>();
                bool endsOnNewline = true;

                await foreach (var update in agent.RunStreamingAsync(nextMessage, ctx.Session))
                {
                    foreach (var content in update.Contents)
                    {
                        switch (content)
                        {
                            case TextContent text when text.Text.Length > 0:
                                Console.Write(text.Text);
                                endsOnNewline = text.Text[^1] == '\n';
                                break;
                            case FunctionCallContent call:
                                Console.WriteLine($"\n[{FormatCall(call)}]");
                                endsOnNewline = true;
                                break;
                            case ToolApprovalRequestContent req:
                                pendingApprovals.Add(req);
                                break;
                            case UsageContent uc:
                                turnUsage.Add(uc.Details);
                                sessionUsage.Add(uc.Details);
                                break;
                        }
                    }

                    if (update.FinishReason is not null && !endsOnNewline)
                    {
                        Console.WriteLine();
                        endsOnNewline = true;
                    }
                }

                if (pendingApprovals.Count == 0) break;

                var responses = new List<AIContent>();
                foreach (var req in pendingApprovals)
                {
                    var approved = ApprovalPrompt.Ask(req, approval);
                    responses.Add(req.CreateResponse(approved, approved ? "user approved" : "user denied"));
                }
                nextMessage = new ChatMessage(ChatRole.User, responses);
            }
            Console.WriteLine(turnUsage.FormatSummary(model));
            Console.WriteLine();

            ctx.Preview ??= input.Length > 60 ? input[..60] + "..." : input;
            await SessionStore.SaveAsync(ctx.SessionId, ctx.CreatedAt, model, ctx.Preview, ctx.Session, agent);
        }

        Console.WriteLine($"\n(session saved: {ctx.SessionId} — resume with: dotnet run -- --resume {ctx.SessionId})");
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

    // Step 7+8: surface yolo / plan state at the prompt so the user can
    // see at a glance which mode they're in.
    private static string PromptLabel(ApprovalState a) => a switch
    {
        { PlanMode: true } => "you (plan) > ",
        { YoloMode: true } => "you (yolo) > ",
        _                  => "you > ",
    };
}
