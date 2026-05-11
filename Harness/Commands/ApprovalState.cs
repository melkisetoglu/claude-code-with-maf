// =============================================================================
//  ApprovalState — process-local memory for the approval gate's UX shortcuts.
//
//  Two distinct mechanisms that landed together in Step 7:
//    - YoloMode      : a blanket "auto-approve everything" toggle, set by the
//                      /yolo slash command. Useful for known-safe workflows
//                      and CI runs; off by default; not persisted to disk.
//    - AlwaysApprove : a per-tool memory ("from now on, write_file is fine"),
//                      populated when the user answers 'a' / 'always' at the
//                      approval prompt. Cleared on process restart — pairing
//                      it with disk persistence belongs with Step 12's
//                      FileMemoryProvider.
//
//  Both are read by Harness/ApprovalPrompt.cs to decide whether to actually
//  prompt or just auto-approve.
// =============================================================================

namespace ClaudeChat.Harness.Commands;

public sealed class ApprovalState
{
    public bool YoloMode { get; set; }

    public HashSet<string> AlwaysApprove { get; } =
        new(StringComparer.Ordinal);
}
