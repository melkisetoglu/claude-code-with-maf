// =============================================================================
//  list_dir — list one directory level. Read-only.
//
//  Pairs with glob (recursive find by pattern) and read_file (open the thing
//  you found). Together they let the model navigate without you pasting paths.
//
//  Output shape (one entry per line):
//      Tools/                (dir)
//      README.md             4322 bytes
//      Program.cs            5230 bytes
//
//  Hidden entries (name starts with '.') are skipped — '.git' / '.idea' / etc.
//  would otherwise drown short answers. The model sees this in the description
//  and can ask differently if it really wants to traverse a dotted dir (no
//  current way to override; we'd add a parameter when it matters).
// =============================================================================

using System.ComponentModel;
using System.Text;

namespace ClaudeChat.Tools;

public static class ListDir
{
    private const int MaxEntries = 200;

    [Description("List the contents of a directory, one level deep. " +
                 "Returns each entry on its own line as either 'name/  (dir)' for subdirectories " +
                 "or 'name  <bytes> bytes' for files. Directories are listed first, then files, " +
                 "both sorted alphabetically. Hidden entries (starting with '.') are skipped. " +
                 "If there are more than 200 entries, output is truncated. " +
                 "If the directory does not exist, returns an error string.")]
    public static string Run(
        [Description("Absolute or relative directory path. Defaults to '.' (current working directory).")]
        string path = ".")
    {
        if (!Directory.Exists(path))
            return $"error: no directory at '{path}'";

        try
        {
            var entries = new DirectoryInfo(path)
                .EnumerateFileSystemInfos()
                .Where(e => !e.Name.StartsWith('.'))
                .OrderBy(e => e is DirectoryInfo ? 0 : 1)   // dirs first
                .ThenBy(e => e.Name, StringComparer.Ordinal);

            var sb = new StringBuilder();
            int count = 0;
            foreach (var e in entries)
            {
                if (count++ >= MaxEntries)
                {
                    sb.AppendLine($"... (truncated; showing first {MaxEntries} entries)");
                    break;
                }
                if (e is DirectoryInfo)
                    sb.AppendLine($"{e.Name}/  (dir)");
                else
                    sb.AppendLine($"{e.Name}  {((FileInfo)e).Length} bytes");
            }
            if (count == 0) return $"(empty directory: '{path}')";
            return sb.ToString().TrimEnd();
        }
        catch (UnauthorizedAccessException) { return $"error: permission denied reading '{path}'"; }
        catch (Exception ex)                { return $"error: {ex.GetType().Name}: {ex.Message}"; }
    }
}
