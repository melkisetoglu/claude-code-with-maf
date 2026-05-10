// =============================================================================
//  write_file — write a string to a file. Approval-required (gated by Step 3).
//
//  Semantics:
//    - Overwrites without asking; the gate is the safety mechanism, not the tool.
//    - Creates missing parent directories. Saves the model from needing a
//      separate mkdir capability.
//    - 100KB cap on contents — same as read_file, for symmetry. The model
//      shouldn't be writing megabyte payloads.
//    - Returns a short summary string ("wrote N bytes to <path>") that the
//      model can fold into its reply.
// =============================================================================

using System.ComponentModel;

namespace ClaudeChat.Tools;

public static class WriteFile
{
    private const int MaxBytes = 100_000;

    [Description("Write text content to a file. Overwrites the file if it exists. " +
                 "Creates missing parent directories. " +
                 "Cap: 100KB (UTF-8 bytes). " +
                 "Returns a confirmation string like 'wrote 234 bytes to README.md'. " +
                 "On error, returns an error string starting with 'error:'.")]
    public static string Run(
        [Description("Absolute or relative file path. Parent directories are created if missing.")]
        string path,
        [Description("UTF-8 text content to write. Capped at 100KB.")]
        string contents)
    {
        if (string.IsNullOrEmpty(path))
            return "error: path is required";

        var byteCount = System.Text.Encoding.UTF8.GetByteCount(contents);
        if (byteCount > MaxBytes)
            return $"error: contents are {byteCount} bytes, exceeds the {MaxBytes}-byte limit for write_file";

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(path, contents);
            return $"wrote {byteCount} bytes to {path}";
        }
        catch (UnauthorizedAccessException) { return $"error: permission denied writing '{path}'"; }
        catch (Exception ex)                { return $"error: {ex.GetType().Name}: {ex.Message}"; }
    }
}
