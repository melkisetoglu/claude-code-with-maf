// =============================================================================
//  ApprovalPrompt — the y/N gate the user sees when an approval-required
//  tool is about to run.
//
//  Default policy: deny unless the user types 'y' or 'yes' (case-insensitive,
//  whitespace-trimmed). Anything else, including empty input, denies. The
//  asymmetry is deliberate: an accidentally-approved destructive action is
//  unrecoverable; an accidentally-denied one is annoying but recoverable.
//
//  Step 7 will replace the inline prompt with a real slash-command/dialog
//  layer that supports "always approve this tool" and a /yolo bypass.
// =============================================================================

using Microsoft.Extensions.AI;

namespace ClaudeChat.Harness;

public static class ApprovalPrompt
{
    public static bool Ask(ToolApprovalRequestContent request)
    {
        var label = request.ToolCall is FunctionCallContent fcc
            ? Format(fcc)
            : request.ToolCall.GetType().Name;

        Console.WriteLine();
        Console.Write($"  approve {label}? [y/N]: ");
        var line = Console.ReadLine() ?? "";
        var trimmed = line.Trim();
        var approved = trimmed.Equals("y", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Equals("yes", StringComparison.OrdinalIgnoreCase);
        Console.WriteLine(approved ? "  → approved\n" : "  → denied\n");
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
