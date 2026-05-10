// =============================================================================
//  edit_file — find-and-replace a literal string inside an existing file.
//  Approval-required (gated by Step 3).
//
//  Semantics:
//    - File must exist; this tool does not create files (use write_file).
//    - old_string is matched literally (no regex). Must appear exactly once
//      in the file or the call fails — ambiguity is treated as an error
//      because guessing which occurrence to replace is unsafe.
//    - replace_all: false by default. Set true to replace every occurrence.
//    - Returns a summary like 'replaced 1 occurrence in <path>' on success.
//    - Empty old_string is rejected (would otherwise silently insert at
//      every position, which is never what's intended).
//
//  Why ambiguity-is-error: models occasionally try to replace something
//  generic ("the import line") that appears in three places. The right fix
//  isn't to guess; it's to ask the model to give more context. The error
//  message tells it how many occurrences were found.
// =============================================================================

using System.ComponentModel;

namespace ClaudeChat.Tools;

public static class EditFile
{
    [Description("Replace a literal string in an existing file. " +
                 "old_string is matched literally (not regex). It must appear EXACTLY ONCE " +
                 "in the file unless replace_all is true; otherwise the call fails with " +
                 "an ambiguity error so you can supply more context. " +
                 "Use write_file to create new files; this tool only edits existing ones. " +
                 "Returns a summary like 'replaced 1 occurrence in <path>' on success, " +
                 "or an error string starting with 'error:'.")]
    public static string Run(
        [Description("Absolute or relative path to an existing file.")]
        string path,
        [Description("Exact literal text to find. Must appear exactly once unless replace_all is true.")]
        string old_string,
        [Description("Replacement text.")]
        string new_string,
        [Description("If true, replace every occurrence instead of failing on multi-match. Default false.")]
        bool replace_all = false)
    {
        if (string.IsNullOrEmpty(path))
            return "error: path is required";
        if (string.IsNullOrEmpty(old_string))
            return "error: old_string is required and cannot be empty";
        if (!File.Exists(path))
            return $"error: no file at '{path}'";

        try
        {
            var content = File.ReadAllText(path);
            var count = CountOccurrences(content, old_string);

            if (count == 0)
                return $"error: old_string not found in '{path}'";

            if (count > 1 && !replace_all)
                return $"error: old_string appears {count} times in '{path}' — supply more context " +
                       "or set replace_all=true to replace every occurrence";

            var newContent = replace_all
                ? content.Replace(old_string, new_string)
                : ReplaceFirst(content, old_string, new_string);

            File.WriteAllText(path, newContent);
            return $"replaced {count} occurrence{(count == 1 ? "" : "s")} in {path}";
        }
        catch (UnauthorizedAccessException) { return $"error: permission denied writing '{path}'"; }
        catch (Exception ex)                { return $"error: {ex.GetType().Name}: {ex.Message}"; }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

    private static string ReplaceFirst(string haystack, string needle, string replacement)
    {
        var idx = haystack.IndexOf(needle, StringComparison.Ordinal);
        return idx < 0
            ? haystack
            : haystack[..idx] + replacement + haystack[(idx + needle.Length)..];
    }
}
