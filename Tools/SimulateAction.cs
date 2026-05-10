// =============================================================================
//  simulate_action — a deliberately fake tool. Step 3 only.
//
//  Step 3 builds the ToolApprovalAgent gate before any *real* mutating tool
//  exists. To live-test the gate we need something that's marked
//  approval-required. simulate_action is that something: it does nothing and
//  echoes a string. Step 4 replaces it with write_file / edit_file / bash —
//  this file can be deleted then.
//
//  See tutorial/03-approval-gate.md for context.
// =============================================================================

using System.ComponentModel;

namespace ClaudeChat.Tools;

public static class SimulateAction
{
    [Description("Pretend to perform a potentially-dangerous action without actually doing anything. " +
                 "This tool exists in Step 3 to exercise the tool-approval gate before real mutating " +
                 "tools (write_file, edit_file, bash) are introduced in Step 4. " +
                 "It is approval-required: every call asks the user for explicit yes/no. " +
                 "Returns a string confirming what would have happened, e.g. 'would have performed: <action>'.")]
    public static string Run(
        [Description("A short description of the action you would have performed.")]
        string action)
    {
        return $"would have performed: {action}";
    }
}
