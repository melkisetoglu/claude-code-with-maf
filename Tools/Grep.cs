// =============================================================================
//  grep — search file contents for a regex. Read-only.
//
//  Output mirrors the Unix grep convention models are trained on:
//      path:line: matched text
//
//  Recursion: if `path` is a directory, descends through it; if it's a file,
//  searches just that file. We skip:
//    - any `.git` directory in the traversal (avoids dumping object hashes)
//    - files that look binary (first 512 bytes contain a NUL byte)
//
//  Three caps prevent runaway output (see "the sessions-folder incident":
//  a single grep across sessions/*.json hit JSON-encoded tool results with
//  single lines >30 KB long, returning ~250 KB of "matches" — enough to
//  blow the model's context budget on the NEXT turn):
//    - MaxMatches:        cap of 100 matching lines total
//    - MaxMatchLineChars: each match line truncated to 500 chars (the
//                         rest replaced with "... <line truncated, N chars total>")
//    - MaxOutputChars:    overall output stops once it reaches 50 KB
// =============================================================================

using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeChat.Tools;

public static class Grep
{
    private const int MaxMatches        = 100;
    private const int MaxMatchLineChars = 500;
    private const int MaxOutputChars    = 50_000;
    private const int BinarySniffBytes  = 512;

    [Description("Search for a regex pattern in file contents. " +
                 "Path can be a file (single-file search) or a directory (recursive). " +
                 "Returns matching lines as 'path:line: matched_text', one per line. " +
                 "Pattern is a .NET regex; case-sensitive by default. " +
                 "The '.git' directory and binary-looking files are skipped automatically. " +
                 "Capped at 100 matching lines, 500 chars per matched line, and 50 KB total output. " +
                 "If the pattern is invalid or the path doesn't exist, returns an error string.")]
    public static string Run(
        [Description("Regex pattern to search for.")]
        string pattern,
        [Description("File or directory to search. Defaults to '.' (current working directory, recursive).")]
        string path = ".")
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return "error: pattern is required";

        Regex regex;
        try { regex = new Regex(pattern, RegexOptions.Compiled); }
        catch (ArgumentException ex) { return $"error: invalid regex: {ex.Message}"; }

        IEnumerable<string> files;
        if (File.Exists(path))
        {
            files = new[] { path };
        }
        else if (Directory.Exists(path))
        {
            files = EnumerateNonGitFiles(path);
        }
        else
        {
            return $"error: no file or directory at '{path}'";
        }

        var sb = new StringBuilder();
        int count = 0;

        foreach (var file in files)
        {
            if (IsLikelyBinary(file)) continue;

            string[] lines;
            try { lines = File.ReadAllLines(file); }
            catch { continue; }   // permission / IO — skip silently

            for (int i = 0; i < lines.Length; i++)
            {
                if (regex.IsMatch(lines[i]))
                {
                    if (count >= MaxMatches)
                    {
                        sb.AppendLine($"... (truncated; showing first {MaxMatches} matches)");
                        return sb.ToString().TrimEnd();
                    }

                    // Per-match line truncation. A single line of e.g. JSON-
                    // encoded prior tool output can be 30 KB+; appending it
                    // verbatim is what blew the model's context window in
                    // the sessions-folder incident referenced in the file
                    // header. Truncate to MaxMatchLineChars and tell the
                    // reader how long the line really was.
                    var trimmed = lines[i].TrimEnd();
                    var display = trimmed.Length <= MaxMatchLineChars
                        ? trimmed
                        : trimmed[..MaxMatchLineChars] +
                          $"... <line truncated, {trimmed.Length} chars total>";
                    sb.AppendLine($"{file}:{i + 1}: {display}");
                    count++;

                    // Overall output budget. After this returns, the model
                    // sees we stopped early and can refine its query.
                    if (sb.Length >= MaxOutputChars)
                    {
                        sb.AppendLine(
                            $"... (truncated; output exceeded {MaxOutputChars} chars — narrow your pattern or path)");
                        return sb.ToString().TrimEnd();
                    }
                }
            }
        }

        return count == 0 ? "no matches" : sb.ToString().TrimEnd();
    }

    private static IEnumerable<string> EnumerateNonGitFiles(string root)
    {
        // Manual recursion so we can prune .git directories cheaply.
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] subdirs;
            string[] files;
            try
            {
                subdirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch { continue; }

            foreach (var f in files) yield return f;
            foreach (var d in subdirs)
            {
                var name = Path.GetFileName(d);
                if (name == ".git") continue;
                stack.Push(d);
            }
        }
    }

    private static bool IsLikelyBinary(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[(int)Math.Min(BinarySniffBytes, fs.Length)];
            if (buf.Length == 0) return false;
            fs.ReadExactly(buf);
            for (int i = 0; i < buf.Length; i++)
                if (buf[i] == 0) return true;
            return false;
        }
        catch { return true; }   // unreadable → treat as binary, skip
    }
}
