// =============================================================================
//  ApprovalPrompt — the y/N gate the user sees when an approval-required
//  tool is about to run.
//
//  Default policy: deny unless the user types 'y' / 'yes' (one-shot approve)
//  or 'a' / 'always' (approve and remember the tool for the session — see
//  Step 7 below). Anything else, including empty input, denies. The
//  asymmetry is deliberate: an accidentally-approved destructive action is
//  unrecoverable; an accidentally-denied one is annoying but recoverable.
//
//  Step 7 adds two ergonomics layers on top of the basic prompt:
//    - /yolo bypass: if ApprovalState.YoloMode is true, auto-approve and
//      print a "[auto-approved (yolo)]" line so the action is still visible.
//    - "Always approve this tool" memory: if the user answers 'a' or
//      'always' at the prompt, the tool's name goes into
//      ApprovalState.AlwaysApprove and every future call to the same tool
//      auto-approves with "[auto-approved (always)]". Process-local, no
//      disk persistence — Step 12's FileMemoryProvider is where that
//      conversation belongs.
//
//  Step 8 adds:
//    - PlanMode auto-deny: if ApprovalState.PlanMode is true, every
//      approval-required tool is auto-denied with "[denied: in plan mode]".
//      This wins over YoloMode and AlwaysApprove — plan mode is the
//      stronger safety guarantee, so it's the first check in the chain.
//
//  The state object is passed in (not a static) so tests can drive it
//  cleanly and so the slash commands can flip it without a singleton.
// =============================================================================

using Microsoft.Extensions.AI;
using ClaudeChat.Harness.Commands;

namespace ClaudeChat.Harness;

public static class ApprovalPrompt
{
    public static bool Ask(ToolApprovalRequestContent request, ApprovalState state)
    {
        var toolName = request.ToolCall is FunctionCallContent fcc ? fcc.Name : null;
        var label = request.ToolCall is FunctionCallContent fcc2
            ? Format(fcc2)
            : request.ToolCall.GetType().Name;

        // Pre-prompt fast paths. Order matters: plan mode beats both yolo
        // and always-approve, because plan mode is the stronger guarantee.
        if (state.PlanMode)
        {
            Console.WriteLine($"  approve {label}? [denied: in plan mode]\n");
            return false;
        }
        if (state.YoloMode)
        {
            Console.WriteLine($"  approve {label}? [auto-approved (yolo)]\n");
            return true;
        }
        if (toolName is not null && state.AlwaysApprove.Contains(toolName))
        {
            Console.WriteLine($"  approve {label}? [auto-approved (always)]\n");
            return true;
        }

        Console.WriteLine();
        Console.Write($"  approve {label}? [y/N/a=always]: ");
        var line = Console.ReadLine() ?? "";
        var trimmed = line.Trim();

        var isAlways = trimmed.Equals("a", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Equals("always", StringComparison.OrdinalIgnoreCase);
        var isYes = trimmed.Equals("y", StringComparison.OrdinalIgnoreCase)
                  || trimmed.Equals("yes", StringComparison.OrdinalIgnoreCase);

        var approved = isYes || isAlways;
        if (isAlways && toolName is not null)
        {
            state.AlwaysApprove.Add(toolName);
            Console.WriteLine($"  → approved (and remembering '{toolName}' for the rest of this session)\n");
        }
        else
        {
            Console.WriteLine(approved ? "  → approved\n" : "  → denied\n");
        }
        return approved;
    }

    // "simulate_action(action=\"rm -rf /\")"
    private static string Format(FunctionCallContent call)
    {
        if (call.Arguments is null || call.Arguments.Count == 0)
            return $"{call.Name}()";

        var args = string.Join(", ",
            call.Arguments.Select(kv => $"{kv.Key}=\"{Truncate(kv.Value?.ToString() ?? "")}\""));
        return $"{call.Name}({args})";
    }

    private static string Truncate(string s, int max = 60) =>
        s.Length <= max ? s : s[..max] + "...";
}
