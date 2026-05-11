# Step 06 — External `agent.json` (model, prompt, tools, approval)

> *Goal: agent behaviour becomes data instead of code. Same binary, multiple profiles — switch with a config file, no recompile.*

Steps 0–5 wired the agent's behaviour into source: the model name, the system prompt, the set of tools, and which of them require approval all lived in `AgentBuilder.cs`. That's fine when there's one agent. It stops scaling the moment you want **the same code running with different settings** — a read-only "code-explainer" profile, a destructive "agentic-cleanup" profile, a single-tool experiment.

Step 6 lifts those four things out of code and into `agent.json`. The rest of the harness — logging, gates, persistence, tooling — is unchanged.

## What you'll have at the end

```bash
$ dotnet run
# Out of the box: same defaults as Step 5; no config file needed.

$ cat > agent.json << 'EOF'
{
  "model": "claude-haiku-4-5",
  "instructions": "You are a read-only code reviewer. Be terse.",
  "tools": { "allow": ["read_file", "list_dir", "glob", "grep"] }
}
EOF

$ dotnet run
# Now: readonly profile auto-discovered from cwd.
# Bash tool no longer registered → input tokens drop ~770 per turn.

$ dotnet run -- --config ./profiles/aggressive.json
# Or: load a profile from anywhere; --config beats cwd discovery.

$ dotnet run -- --config /tmp/typo.json
agent.json config error: unknown tool name(s) in agent.json tools.allow:
frobnicate. known tools: bash, edit_file, glob, grep, list_dir, read_file,
write_file.
# Typos fail loudly at startup, before reaching the model.
```

## MAF concepts introduced

**None.** Step 6 doesn't use any new MAF API — it teaches the workshop pattern of *lifting in-code knobs into config without breaking the abstractions below*. The data layer (a record), the loader (`System.Text.Json`), and the resolver (filter + wrap in `AgentBuilder`) are plain .NET.

What it does add is a **named tool registry** in `AgentBuilder.cs` — a dictionary from tool name → factory — that previously was an inline `new List<AITool> { ... }`. Once tools are addressable by string, config can pick from them.

## Setup

No new packages. `System.Text.Json` is already in the framework.

One new file, one folder:

```
Config/AgentConfig.cs        — the record + JSON loader
```

## Walkthrough

### The config record

*In [`Config/AgentConfig.cs`](../Config/AgentConfig.cs).*

```csharp
public sealed record AgentConfig(
    string? Model,
    string? Instructions,
    ToolsConfig? Tools);

public sealed record ToolsConfig(
    IReadOnlyList<string>? Allow,
    IReadOnlyList<string>? RequireApproval);
```

Every field nullable. **The missing-field path is the default path**; an empty `agent.json` (`{}`) is identical to no `agent.json`, which is identical to Step 5. **Out-of-the-box behaviour is preserved by construction**, not by careful merging.

Property naming: camelCase in JSON, PascalCase in C#, bridged with `PropertyNamingPolicy = JsonNamingPolicy.CamelCase` so the JSON reads as `requireApproval` rather than `RequireApproval`. Matches Claude Code's own settings convention.

The loader allows JSON comments and trailing commas — small kindness for users editing the file by hand:

```csharp
private static readonly JsonSerializerOptions JsonOpts = new()
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
};
```

### Two entry points: explicit path and cwd discovery

```csharp
public static AgentConfig LoadFromPath(string path);  // explicit, throws if missing
public static AgentConfig? TryLoadFromCwd();          // ./agent.json, null if missing
```

The two behave differently on missing-file:

| Entry point | Missing file | Malformed JSON |
|---|---|---|
| `LoadFromPath` (used by `--config <path>`) | **throws** `FileNotFoundException` | throws `JsonException` |
| `TryLoadFromCwd` (used by Program.cs auto-discovery) | **returns null** silently | throws |

The asymmetry is intentional. **Explicit means "this file should exist"** — a typo in `--config ./agent-readonly.jso` is a bug, not a fallback. **Cwd discovery is opt-in by file presence** — running outside a configured project shouldn't error.

Both throw on malformed JSON. Silent fallback to defaults when the file is *present* but *broken* would be a worse bug than crashing on startup — it would silently use the wrong agent for hours before you noticed.

### Tool registry

*In [`Agent/AgentBuilder.cs`](../Agent/AgentBuilder.cs).*

The hardcoded `new List<AITool> { … }` from Steps 1–5 becomes a named registry:

```csharp
public static readonly IReadOnlyDictionary<string, Func<AIFunction>> ToolRegistry =
    new Dictionary<string, Func<AIFunction>>
    {
        ["read_file"]  = () => AIFunctionFactory.Create(ReadFile.Read,  name: "read_file"),
        ["list_dir"]   = () => AIFunctionFactory.Create(ListDir.Run,    name: "list_dir"),
        ["glob"]       = () => AIFunctionFactory.Create(Glob.Run,       name: "glob"),
        ["grep"]       = () => AIFunctionFactory.Create(Grep.Run,       name: "grep"),
        ["write_file"] = () => AIFunctionFactory.Create(WriteFile.Run,  name: "write_file"),
        ["edit_file"]  = () => AIFunctionFactory.Create(EditFile.Run,   name: "edit_file"),
        ["bash"]       = () => AIFunctionFactory.Create(Bash.Run,       name: "bash"),
    };
```

Three reasons it's a dict of factories rather than a list of instances:

1. **Factories**, not instances — Step 11+ will want to defer construction until the agent is built; some tools might depend on services not yet available.
2. **String-addressable** — `agent.json` references tools by name, not by position. Dict lookup is the natural shape.
3. **Public** — tests and future config-loaders can inspect the set of known tools to validate against, without us maintaining a separate "known names" list.

Adding a new tool to the workshop is now strictly "one line in the registry." Adding a new tool *and remembering to put it in the default approval set*? The chapter on Step 4 promised the gate was "one wrapper away"; Step 6 makes that promise data:

```csharp
public static readonly IReadOnlySet<string> DefaultRequireApproval =
    new HashSet<string>(StringComparer.Ordinal) { "write_file", "edit_file", "bash" };
```

### Resolution semantics

`AgentBuilder.ResolveTools(ToolsConfig?)` is where the config becomes a real `List<AITool>`. It encodes four rules:

1. **`allow` defaults to everything.** No field → all seven tools registered.
2. **`allow` is exhaustive.** Listed tools are registered; unlisted ones aren't. Empty `[]` = chat with no tools.
3. **`requireApproval` defaults to the safe set (intersected with `allow`).** No field → `{write_file, edit_file, bash}` *that are in `allow`*. So a read-only profile (`allow: [read_file]`) defaults to *no* gating, not "gating tools that aren't here."
4. **Explicit `requireApproval` is strictly validated.** If you name a tool in `requireApproval`, it must be in `allow`. Typos there die loudly the same way typos in `allow` die loudly.

That last asymmetry — default-set is silently intersected, explicit-set is strictly validated — is the kind of thing that's annoying to discover by surprise. **Defaults adapt; explicits don't.**

```csharp
HashSet<string> requireApproval;
if (tools?.RequireApproval is null)
{
    // Defaulted: silently restrict the safe set to what's actually allowed.
    requireApproval = new HashSet<string>(
        DefaultRequireApproval.Where(name => allow.Contains(name)),
        StringComparer.Ordinal);
}
else
{
    // Explicit: strict validation.
    requireApproval = new HashSet<string>(tools.RequireApproval, StringComparer.Ordinal);
    var notInAllow = requireApproval.Where(name => !allow.Contains(name)).ToList();
    if (notInAllow.Count > 0)
        throw new InvalidOperationException(/* ... */);
}
```

### Precedence for the model name

```
ANTHROPIC_DEPLOYMENT_NAME env  >  config.model  >  built-in "claude-haiku-4-5"
```

Env wins because it's the most explicit per-invocation knob. `ANTHROPIC_DEPLOYMENT_NAME=claude-sonnet-4-6 dotnet run` should beat whatever `agent.json` says — you're temporarily testing a model. Config beats hardcoded because that's the whole point of config. Built-in is the floor.

### Wiring it together

*In [`Program.cs`](../Program.cs).*

```csharp
AgentConfig? agentConfig;
try
{
    agentConfig = configPath != null
        ? AgentConfigLoader.LoadFromPath(configPath)
        : AgentConfigLoader.TryLoadFromCwd();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to load config: {ex.Message}");
    return 1;
}

var modelEnv = Environment.GetEnvironmentVariable("ANTHROPIC_DEPLOYMENT_NAME")?.Trim();
var model = !string.IsNullOrEmpty(modelEnv)
    ? modelEnv
    : agentConfig?.Model ?? AgentBuilder.DefaultModel;

AIAgent agent;
try
{
    agent = AgentBuilder.Build(
        apiKey,
        agentConfig,
        loggerFactory,
        enableOtel,
        modelOverride: !string.IsNullOrEmpty(modelEnv) ? modelEnv : null);
}
catch (InvalidOperationException ex)   // unknown tool / bad subset
{
    Console.Error.WriteLine($"agent.json config error: {ex.Message}");
    return 1;
}
```

Note `model` is resolved twice — once for surfacing to `ChatLoop` (the per-turn cost line + session metadata want it), once inside `AgentBuilder` itself. The two paths mirror each other; the call site explicitly passes `modelOverride` so the resolution rule isn't duplicated logic-wise, just lookup-wise.

The two `try` blocks separate concerns: **file-load errors** (path missing, JSON broken) vs **semantic errors** (unknown tool name, requireApproval not a subset of allow). Each gets a tagged error message starting with `Failed to load config:` or `agent.json config error:`. Different fixes, different fingerprints.

## Verify

```bash
dotnet build
dotnet test                 # 99 unit tests now (16 new across AgentConfig + ToolRegistry)
```

Live tests:

```bash
# Defaults — should behave exactly like Step 5.
dotnet run
you > one word: hi
claude > Hi.
(turn: 2033 in + 4 out, $0.0021)

# A readonly profile auto-discovered from cwd.
cat > agent.json << 'EOF'
{
  "instructions": "Read-only assistant.",
  "tools": { "allow": ["read_file", "list_dir", "glob", "grep"] }
}
EOF
dotnet run
you > can you write a file?
# Expect: model explains it doesn't have write_file/bash; input tokens drop.

# A specific profile from a path.
dotnet run -- --config /tmp/aggressive.json

# A bad tool name fails loud.
echo '{ "tools": { "allow": ["frobnicate"] } }' > /tmp/bad.json
dotnet run -- --config /tmp/bad.json
# Expect: "agent.json config error: unknown tool name(s) ... frobnicate. known tools: ...", exit 1.

rm agent.json /tmp/bad.json
```

## Pitfalls

### Read-only profile means write tools shouldn't be in the default approval set

`requireApproval` defaults to *the intersection* of `{write_file, edit_file, bash}` with `allow`, not the raw default set. A read-only profile that allows only `read_file` would otherwise fail with "tools in requireApproval not in allow" — a confusing error for what is just "I asked for a subset, the default expanded badly." The implementation handles this; the test `Allow_subset_registers_only_those_tools` enforces it.

### Auto-discovery is cwd-relative, not project-relative

`./agent.json` is read from `Environment.CurrentDirectory`, not from the .csproj location. Running `dotnet run` from a parent directory means the config isn't auto-found. That's intentional: a user who `cd`s to a side directory shouldn't pick up unexpected config from the project root. **Use `--config <path>` if you want determinism.**

### Sessions survive tool changes; the model doesn't know yet

Resuming a session built under `allow: [bash]` after switching to a profile without bash works — `ToolApprovalAgent` and the inner agent both just see fewer tools. But the session's *history* may reference past tool calls the model expects to be able to make again. The model may try, the framework returns an "unknown tool" error, the model adapts. **Resuming across profile changes is "works" not "seamless."** Step 11+ (memory / project context) is where this needs revisiting.

### `--config <path>` doesn't merge — it replaces

There's no "base + override" layering today. A profile is the complete picture. Multi-file inheritance is a real thing some users will want; it's a stretch exercise.

### Comments in JSON aren't standard

`System.Text.Json` supports them when configured (`ReadCommentHandling.Skip`), but a JSON validator running against `agent.json` will reject them. Either keep your config comment-free for tooling, or accept that schema validation may complain.

### `defaultMaxTokens` and per-tool config are NOT covered

`agent.json` controls model name, system prompt, tool allowlist, approval rules — that's it. `ReadFile.MaxBytes`, `Bash.TimeoutMs`, `Glob.MaxMatches`: still hardcoded. Adding per-tool config would mean passing config down into each tool's `[Description]`-decorated method or moving tools to instance classes — out of scope for Step 6.

## Stretch exercises

- **Profiles directory.** `dotnet run -- --profile readonly` resolves to `./profiles/readonly.json`. One layer of indirection above `--config`.
- **Long prompts from a separate file.** Add `instructionsPath` that loads `prompt.md` so prompts aren't trapped in JSON strings.
- **Layered configs.** Merge `./agent.json` (committed) with `./agent.local.json` (gitignored) so per-developer overrides are easy.
- **`--print-config`** flag — print the resolved config (including defaults applied) and exit. Lets a user see what their agent actually looks like.
- **JSON schema file** for editor autocomplete, so `agent.json` typing is a delight in VS Code / JetBrains.
- **Per-tool config.** A `tools.options` map: `{ "read_file": { "maxBytes": 50000 }, "bash": { "timeoutMs": 60000 } }`. Threading the values down through each tool's method is the interesting part; you'd probably move tools to instance classes.

## Where the seams are

What this step deliberately doesn't have:

- **No profile inheritance / layering.** Single file, complete picture.
- **No per-tool config.** Hardcoded inside each tool.
- **No `model` env var beyond `ANTHROPIC_DEPLOYMENT_NAME`.** Programmatic precedence only.
- **No watch-reload.** Edit `agent.json`, restart the process.
- **No schema generation / autocomplete.** Hand-edit + JSON syntax errors.

## Next

→ **Step 07 — Slash commands** *(planned)*

Up next: replace the inline `if (input == "/exit") …` chain with a real dispatcher. `/help`, `/tools`, `/cost`, `/model`, `/sessions` arrive; Step 7 is also where the "always approve this tool" and `/yolo` ergonomics from Steps 3–4 finally land.
