// =============================================================================
//  glob — find files by pattern. Read-only.
//
//  Powered by Microsoft.Extensions.FileSystemGlobbing's `Matcher`. We accept a
//  single include pattern and an optional root directory. `**` matches any
//  number of directory segments; `*` matches any name segment; `?` matches a
//  single character.
//
//  We auto-exclude `.git/**` so the model isn't drowned in object hashes when
//  it greps a project. Other dotted directories (`.idea`, `.vs`) are not
//  excluded — if you don't want those, pass a more specific pattern. Build
//  artefacts like `bin/`, `obj/`, `node_modules/` are NOT excluded: they're
//  legitimate user code in many projects, and the model can be more specific
//  if it doesn't want them.
//
//  Result paths are relative to `root`, sorted, capped at 200.
// =============================================================================

using System.ComponentModel;
using Microsoft.Extensions.FileSystemGlobbing;

namespace ClaudeChat.Tools;

public static class Glob
{
    private const int MaxMatches = 200;

    [Description("Find files matching a glob pattern, recursively under a root directory. " +
                 "Pattern syntax: '*' matches any name segment, '**' matches any number of " +
                 "directory levels, '?' matches a single character. " +
                 "Examples: '*.cs' (top-level only), '**/*.cs' (any depth), 'src/**/Test*.cs'. " +
                 "Returns matching paths relative to root, one per line, sorted. " +
                 "The '.git' directory is automatically excluded. Capped at 200 matches.")]
    public static string Run(
        [Description("Glob pattern to match files against.")]
        string pattern,
        [Description("Root directory to search from. Defaults to '.' (current working directory).")]
        string root = ".")
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return "error: pattern is required";
        if (!Directory.Exists(root))
            return $"error: no directory at '{root}'";

        try
        {
            var matcher = new Matcher(StringComparison.Ordinal);
            matcher.AddInclude(pattern);
            matcher.AddExclude("**/.git/**");

            var result = matcher.GetResultsInFullPath(root)
                .Select(full => Path.GetRelativePath(root, full))
                .OrderBy(p => p, StringComparer.Ordinal)
                .Take(MaxMatches + 1)
                .ToList();

            if (result.Count == 0) return "no matches";

            var truncated = result.Count > MaxMatches;
            if (truncated) result.RemoveAt(MaxMatches);

            var body = string.Join('\n', result);
            return truncated
                ? body + $"\n... (truncated; showing first {MaxMatches} matches)"
                : body;
        }
        catch (Exception ex) { return $"error: {ex.GetType().Name}: {ex.Message}"; }
    }
}
