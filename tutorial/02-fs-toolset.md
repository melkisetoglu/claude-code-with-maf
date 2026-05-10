# Step 02 — File-system toolset: `list_dir`, `glob`, `grep`

> *Goal: the model can navigate the codebase blindfolded — list directories, find files by pattern, search by content — without you pasting paths.*

In Step 1 the model could read a file you handed it the path for. That's still chat with extra steps. The leap in Step 2 is that the model now **finds the path itself**. Three tools — `list_dir`, `glob`, `grep` — compose into the navigation primitive. Combined with `read_file`, this is the read-only half of Claude Code's filesystem surface.

## What you'll have at the end

```
$ dotnet run
Started new session: 89a9e181
you > how is the AIAgent wired up in this project? trace it briefly.
claude > I'll help you trace how the AIAgent is wired up. Let me start by exploring the project structure.
[list_dir: path="."]
Now let me look at the main entry point and the Agent directory:
[read_file: path="Program.cs"]
[list_dir: path="Agent"]
Now let me look at the AgentBuilder:
[read_file: path="Agent/AgentBuilder.cs"]
Perfect! Here's a brief trace... [accurate summary of the code]
```

Four tool calls in a single turn, interleaved with reasoning text, ending in a synthesis the model wrote based on what it read. **This is the agentic loop scaling.**

## MAF concepts introduced

| Concept | What it is |
|---|---|
| Multi-arg `[Description]` schema | A tool method with two or more parameters generates a richer JSON schema — the model can pass `pattern` and `path` separately. |
| Optional parameters with defaults | `Run(string pattern, string root = ".")` — the schema marks `root` as optional. The model usually omits it. |
| Tool composition | One turn can call several tools in sequence. M.E.AI's `FunctionInvokingChatClient` keeps invoking and feeding results back until the model emits a final text response. **No agent loop in our code.** |
| Result truncation as a contract | When a tool's output is capped, *say so in the description* (`"Capped at 200 matches"`) and *say so in the result* (`"... (truncated; showing first 200 matches)"`). The model treats it as a hint to refine the query, not as missing data. |

## Setup

One new package — for `Glob` we use Microsoft's standard glob matcher rather than hand-rolling one:

```bash
dotnet add package Microsoft.Extensions.FileSystemGlobbing
```

The package is small (~30KB) and MS-published. Hand-rolling `**` matching has subtle edge cases around dot-handling and root anchoring; not worth it for a workshop step that's about *tools*, not glob algorithms.

Three new files in `Tools/`:

```
Tools/ListDir.cs
Tools/Glob.cs
Tools/Grep.cs
```

## Walkthrough

### Tool 1 — `list_dir`

*In [`Tools/ListDir.cs`](../Tools/ListDir.cs).*

```csharp
[Description("List the contents of a directory, one level deep. " +
             "Returns each entry on its own line as either 'name/  (dir)' for subdirectories " +
             "or 'name  <bytes> bytes' for files. Directories are listed first, then files, " +
             "both sorted alphabetically. Hidden entries (starting with '.') are skipped. " +
             "If there are more than 200 entries, output is truncated. " +
             "If the directory does not exist, returns an error string.")]
public static string Run(
    [Description("Absolute or relative directory path. Defaults to '.' (current working directory).")]
    string path = ".") { /* ... */ }
```

Three deliberate design choices, each saying something about how to write tools well:

1. **One level, not recursive.** Recursion belongs in `glob`. A `list_dir` that recurses is a `glob` with a worse interface. Don't merge tools that have different shapes; the model picks better when they're cleanly distinct.
2. **Skip dotfiles by default.** `.git/`, `.idea/`, `.DS_Store` would otherwise drown short answers. Document the choice in the description so the model knows what it's *not* getting back.
3. **Default `path = "."`.** The model usually wants cwd. Saving it from typing `path="."` every time is one less thing it can get wrong.

### Tool 2 — `glob`

*In [`Tools/Glob.cs`](../Tools/Glob.cs).*

```csharp
[Description("Find files matching a glob pattern, recursively under a root directory. " +
             "Pattern syntax: '*' matches any name segment, '**' matches any number of " +
             "directory levels, '?' matches a single character. " +
             "Examples: '*.cs' (top-level only), '**/*.cs' (any depth), 'src/**/Test*.cs'. " +
             "Returns matching paths relative to root, one per line, sorted. " +
             "The '.git' directory is automatically excluded. Capped at 200 matches.")]
public static string Run(string pattern, string root = ".") { /* ... */ }
```

The implementation hands the pattern to Microsoft's `Matcher`:

```csharp
var matcher = new Matcher(StringComparison.Ordinal);
matcher.AddInclude(pattern);
matcher.AddExclude("**/.git/**");
var paths = matcher.GetResultsInFullPath(root)
    .Select(full => Path.GetRelativePath(root, full))
    .OrderBy(p => p, StringComparer.Ordinal)
    .Take(MaxMatches + 1)
    .ToList();
```

A couple of things to notice:

- **`AddExclude("**/.git/**")` is the only opinion baked in.** I considered also excluding `bin/`, `obj/`, `node_modules/` — but those are legitimate user code in many projects. The model can be more specific (`src/**/*.cs`) if it wants to skip them. **Hidden behavior is worse than verbose results.**
- **`Take(MaxMatches + 1)`** lets us know whether the cap was *hit*, not just *reached*. If we get 201 results back, we know to print "truncated."
- **Paths returned are relative to `root`.** That's what the model needs to feed into `read_file` later.

### Tool 3 — `grep`

*In [`Tools/Grep.cs`](../Tools/Grep.cs).*

The output mirrors the Unix grep convention models are heavily trained on:

```
Agent/AgentBuilder.cs:23: AnthropicClient client = new() { ApiKey = apiKey };
Program.cs:6: //    - AnthropicClient        (from the official Anthropic SDK package)
```

`path:line: text` — predictable, parseable in the model's head, and lets it pipe `read_file` calls afterwards if it wants more context.

The recursion pruning is hand-written rather than `Directory.EnumerateFiles(SearchOption.AllDirectories)`:

```csharp
var stack = new Stack<string>();
stack.Push(root);
while (stack.Count > 0)
{
    var dir = stack.Pop();
    var subdirs = Directory.GetDirectories(dir);
    var files = Directory.GetFiles(dir);
    foreach (var f in files) yield return f;
    foreach (var d in subdirs)
    {
        var name = Path.GetFileName(d);
        if (name == ".git") continue;     // prune
        stack.Push(d);
    }
}
```

The built-in recursive enumerator can't prune mid-traversal — once you tell it `AllDirectories` it visits everything, and you'd skip `.git/` per-file (slow and silly when `.git/` has 50,000 objects). A hand-written stack-based traversal is one of those rare moments where the standard library is the wrong shape.

We also skip files that *look* binary — first 512 bytes contain a NUL byte. Crude but effective: it skips images, compiled binaries, and most non-text formats without misclassifying real source code.

### Registering them all

*In [`Agent/AgentBuilder.cs`](../Agent/AgentBuilder.cs).*

```csharp
var tools = new List<AITool>
{
    AIFunctionFactory.Create(ReadFile.Read, name: "read_file"),
    AIFunctionFactory.Create(ListDir.Run,  name: "list_dir"),
    AIFunctionFactory.Create(Glob.Run,     name: "glob"),
    AIFunctionFactory.Create(Grep.Run,     name: "grep"),
};

return client.AsAIAgent(
    model: model,
    name: "ClaudeChat",
    instructions: "You are a helpful assistant. Keep replies concise. " +
                  "You can navigate the user's project with these tools: " +
                  "read_file (open a file), list_dir (one directory level), " +
                  "glob (find files by pattern, recursive), grep (search file contents). " +
                  "Prefer glob/grep over guessing paths. " +
                  "Use specific roots/patterns to avoid noisy results.",
    tools: tools);
```

Three observations:

- **Each tool gets one line.** Adding the next one in Step 4 will be one more line. This is the seam paying off — Program.cs doesn't change.
- **The instructions name each tool and a one-line role.** Without that, models sometimes guess paths instead of calling `glob`. With it, you almost never see "I think the file is probably at..." — the model just looks.
- **"Use specific roots/patterns to avoid noisy results."** This is a soft instruction. The hard one is the cap on each tool's output. The soft one is a hint that biases behavior; the cap is what catches it when the hint fails.

### Display upgrade in `ChatLoop`

*In [`Harness/ChatLoop.cs`](../Harness/ChatLoop.cs).*

In Step 1 we cheated:

```csharp
var firstArg = call.Arguments?.Values.FirstOrDefault();
Console.WriteLine($"\n[{call.Name}: {firstArg}]");
```

That worked for `read_file(path)` — one arg, easy. For `grep(pattern, path)` it loses the pattern context. Step 2 generalizes:

```csharp
private static string FormatCall(FunctionCallContent call)
{
    if (call.Arguments is null || call.Arguments.Count == 0)
        return call.Name;

    var args = string.Join(", ",
        call.Arguments.Select(kv => $"{kv.Key}=\"{Truncate(kv.Value?.ToString() ?? "")}\""));
    return $"{call.Name}: {args}";
}
```

Output looks like:

```
[list_dir: path="Tools"]
[glob: pattern="**/*.cs", root="."]
[grep: pattern="AnthropicClient", path="."]
[read_file: path="README.md"]
```

`Truncate(s, max=60)` clips long values so a multi-line regex doesn't dump into the terminal. **Function results are still not displayed** (would be either redundant or huge); Step 4 changes that for mutation tools, where seeing what happened actually matters.

## Verify

```bash
dotnet build
dotnet test          # 31 unit tests across the four tools' edge cases
```

Live tests:

```bash
dotnet run
you > list the Tools directory
# Expect: [list_dir: path="Tools"] line, then a four-file summary

you > /clear
you > find every .cs file under Persistence
# Expect: [glob: pattern="..."] line, then Persistence/SessionStore.cs

you > /clear
you > where is AnthropicClient used in this codebase?
# Expect: [grep: pattern="AnthropicClient"] line, then path:line listings

you > /clear
you > how is the AIAgent wired up?
# Expect: 3-5 tool calls (list_dir / read_file mixed), ending in a synthesis
```

The fourth one is the test that matters: tool **composition** in a single turn.

## Pitfalls

### Tools that overlap confuse the model

If `list_dir` recursed and `glob` also recursed, the model would oscillate between them. Cleanly distinct shapes — *one level vs. pattern-based descent* — make the choice obvious. **The taxonomy of your tools is part of their interface.**

### `Directory.EnumerateFiles(SearchOption.AllDirectories)` is the wrong tool for grep

It can't prune `.git/`. You'd visit 50,000 git objects, skip them per-file (binary detection), and burn seconds for nothing. Hand-write the stack-based traversal when you need pruning.

### The cap is part of the contract — make it visible

If a tool truncates silently, the model treats truncated output as complete and lies confidently. Append `... (truncated; showing first N)` so the model knows to refine its query, AND mention the cap in the `[Description]` so it knows up front.

### `.git` exclusion is the only "magic" we baked in

Resisting the urge to also exclude `bin/`, `obj/`, `node_modules/` is harder than it sounds. They produce noisy globs. But excluding them by default would mean the model can't find generated code, build outputs, or third-party sources when it actually wants them. **Make the user's project visible; let the model be specific.**

### Multi-arg display matters more than you'd think

`[grep: pattern="AnthropicClient", path="src/"]` reads instantly. `[grep: AnthropicClient]` (Step-1 first-arg hack) hides which directory was searched. When the model misbehaves, the trace is your only window into *why* — make it readable.

## Stretch exercises

- **Add a `case_insensitive: bool` parameter to `grep`.** Watch how the model uses it.
- **`max_results` parameter on each tool**, capped to the hardcoded ceiling. Lets the model ask for a smaller batch when it knows it doesn't need all 200.
- **Track tool-call counts per turn.** Print `(4 tool calls)` after the answer. Sets up Step 5's cost display.
- **Refactor Step 1's `read_file` to accept `start_line` / `end_line`.** Compare what the model does when it can target a slice vs. always reading whole files.
- **A `find` tool that combines `glob` and `grep`** — find files where the *path* matches one pattern and the *content* matches another. Does the model use it more than calling glob and grep separately?

## Where the seams are

Things this step deliberately doesn't have:

- **No path scoping.** Step 3.
- **No mutation.** Step 4.
- **No `.gitignore` awareness.** Honest about the cost: parsing gitignore is non-trivial in .NET, and the workshop has no Step for "ergonomics polish." Fold into Step 11 (project-context auto-load) when the time is right.
- **No ranked grep results.** Real Claude Code uses [ripgrep](https://github.com/BurntSushi/ripgrep) under the hood — fast, ranked, gitignore-aware. We're using `Regex` over `File.ReadAllLines`. Adequate for a workshop on dozens of files; not for a real codebase. Step 16 (MCP) is where you'd point at a real ripgrep binary.

## Next

→ **Step 03 — tool-approval gate (`ToolApprovalAgent`)** *(planned)*

That step is where we stop trusting the model with raw filesystem access. Mutations go through a yes/no prompt. Path scoping lands. After Step 3, "the agent can do anything you don't stop it from doing" stops being true.
