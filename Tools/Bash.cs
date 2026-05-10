// =============================================================================
//  bash — run a shell command. Approval-required (gated by Step 3).
//
//  This is the genuinely-dangerous tool. write_file and edit_file have
//  bounded blast radius (one file each); bash is unbounded — it can do
//  anything the user account can. The gate is the only safety mechanism.
//
//  Caps:
//    - 30s timeout. Catches hangs and 'sleep infinity'-style misbehaviour.
//      Process is killed on timeout (and its children, best-effort).
//    - 50KB combined stdout+stderr. Catches 'yes | head -c 1G'-style
//      output bombs that would blow up the model's context.
//
//  We don't do command parsing or pattern blacklisting. Static analysis of
//  shell strings is defeated by absolute paths, command substitution,
//  eval, etc., and a pattern blacklist would teach a false sense of safety.
//  Real isolation comes from running the workshop in a VM/container or as
//  a low-privilege user.
// =============================================================================

using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace ClaudeChat.Tools;

public static class Bash
{
    private const int TimeoutMs = 30_000;
    private const int MaxOutputBytes = 50_000;

    [Description("Run a shell command via /bin/bash -c. Returns combined stdout+stderr. " +
                 "Times out after 30 seconds. Output is capped at 50KB (truncated). " +
                 "WARNING: this is unrestricted shell access — the model can do anything " +
                 "the user account can (delete files, modify configs, network requests, etc.). " +
                 "Always review the command shown in the approval prompt before approving. " +
                 "Prefer specific commands over open-ended ones; prefer the file tools " +
                 "(write_file, edit_file) when an edit is what you need.")]
    public static string Run(
        [Description("The shell command line. Passed as a single argument to /bin/bash -c.")]
        string command,
        [Description("Working directory. Defaults to '.' (current working directory).")]
        string cwd = ".")
    {
        if (string.IsNullOrWhiteSpace(command))
            return "error: command is required";
        if (!Directory.Exists(cwd))
            return $"error: no directory at '{cwd}'";

        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            ArgumentList = { "-c", command },
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
            if (proc == null) return "error: failed to start /bin/bash";

            // Read stdout/stderr concurrently into a shared buffer with a cap.
            var output = new StringBuilder();
            var lockObj = new object();
            var truncated = false;

            void Append(string? line)
            {
                if (line is null) return;
                lock (lockObj)
                {
                    if (truncated) return;
                    if (output.Length + line.Length + 1 > MaxOutputBytes)
                    {
                        var room = MaxOutputBytes - output.Length;
                        if (room > 0) output.Append(line.AsSpan(0, Math.Min(room, line.Length)));
                        output.Append("\n... (truncated; output exceeded 50KB)");
                        truncated = true;
                        return;
                    }
                    output.AppendLine(line);
                }
            }

            proc.OutputDataReceived += (_, e) => Append(e.Data);
            proc.ErrorDataReceived  += (_, e) => Append(e.Data);
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            if (!proc.WaitForExit(TimeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                return $"error: command timed out after {TimeoutMs / 1000}s and was killed\n" +
                       output.ToString().TrimEnd();
            }

            // Drain any remaining buffered output.
            proc.WaitForExit();

            var combined = output.ToString().TrimEnd();
            return proc.ExitCode == 0
                ? (combined.Length == 0 ? "(no output)" : combined)
                : $"(exit code {proc.ExitCode})\n{combined}";
        }
        catch (Exception ex)
        {
            return $"error: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            proc?.Dispose();
        }
    }
}
