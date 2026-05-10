// =============================================================================
//  read_file — the first tool. Read-only, no path scoping (Step 3 adds the
//  approval gate).
//
//  Why a tool turns this into an agent: with chat-only the model could only
//  guess at what's in your codebase. With read_file it can ground answers in
//  actual file contents — and the model decides when to call, what to read,
//  and how to fold results into its reply. That's the agentic loop.
//
//  Shape of a MAF/M.E.AI tool:
//    1. Plain .NET method, decorated with [Description] on the method and on
//       each parameter. The descriptions become the tool/parameter schema the
//       model sees — write them like docs for the model, not for humans.
//    2. AIFunctionFactory.Create(method) wraps the method as an AIFunction.
//    3. The AIFunction is passed to AsAIAgent(..., tools: [...]).
//    4. M.E.AI's FunctionInvokingChatClient auto-invokes when the model emits
//       a tool-call. We don't write the call→exec→result loop ourselves.
//
//  Errors-as-strings: when the read fails (missing file, too big, encoding
//  problem), we return a short error string instead of throwing. The model
//  reads the result, sees the error, and recovers — usually by trying a
//  different path or telling the user. Throwing would crash the turn.
// =============================================================================

using System.ComponentModel;

namespace ClaudeChat.Tools;

public static class ReadFile
{
    // Roughly 100 KB. Files larger than this drop tens of thousands of tokens
    // into context — usually a mistake. Easy to lift if we hit a real need.
    private const int MaxBytes = 100_000;

    [Description("Read the contents of a text file at the given path. " +
                 "Returns the file's text. " +
                 "If the file is missing, too large (>100KB), or not readable as text, " +
                 "returns a short error message instead of throwing.")]
    public static string Read(
        [Description("Absolute or relative file path. Relative paths resolve against the current working directory.")]
        string path)
    {
        try
        {
            if (!File.Exists(path))
                return $"error: no file at '{path}'";

            var info = new FileInfo(path);
            if (info.Length > MaxBytes)
                return $"error: file '{path}' is {info.Length} bytes, exceeds the {MaxBytes}-byte limit for read_file";

            return File.ReadAllText(path);
        }
        catch (UnauthorizedAccessException)
        {
            return $"error: permission denied reading '{path}'";
        }
        catch (Exception ex)
        {
            return $"error: {ex.GetType().Name}: {ex.Message}";
        }
    }
}
