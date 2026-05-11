// =============================================================================
//  ApprovalState — process-local memory for the approval gate's UX shortcuts.
//
//  Three distinct mechanisms, all read by Harness/ApprovalPrompt.cs:
//    - YoloMode      : (Step 7) a blanket "auto-approve everything" toggle,
//                      set by /yolo. Useful for known-safe workflows and CI;
//                      off by default; not persisted.
//    - AlwaysApprove : (Step 7) a per-tool memory ("from now on, write_file
//                      is fine"), populated when the user answers 'a' /
//                      'always' at the approval prompt.
//    - PlanMode      : (Step 8) a temporary read-only state set by /plan.
//                      While on, approval-required tools are auto-DENIED so
//                      the model can only explore and produce a plan. Wins
//                      over YoloMode and AlwaysApprove — plan mode is the
//                      stronger safety guarantee.
//
//  YoloMode and PlanMode are mutually exclusive — enabling one disables the
//  other (enforced in the /yolo and /plan slash commands).
// =============================================================================

namespace ClaudeChat.Harness.Commands;

public sealed class ApprovalState
{
    public bool YoloMode { get; set; }

    public bool PlanMode { get; set; }

    public HashSet<string> AlwaysApprove { get; } =
        new(StringComparer.Ordinal);
}
