# Step 11 — Project-context auto-load via `AgentSkillsProvider`

> *Goal: when the agent starts in a repo that has a `./skills/<name>/SKILL.md` file, the model becomes aware of that skill — its name and what it's for — and can load the body on demand, without us pasting context into every prompt.*

Steps 0–10 built up an agent that has tools, an approval gate, observability, configuration, slash commands, plan mode, streaming polish, and context compaction. What it doesn't have yet: **memory of the project it's working in**. Every new session starts fresh — the model doesn't know your repo's conventions, your commit-prefix scheme, your folder layout.

Step 11 fixes the first slice of that with the framework's **`AgentSkillsProvider`** — a near-1:1 port of Anthropic's Claude Code Skills system to MAF. A "skill" is a folder under `./skills/` with a `SKILL.md` manifest (YAML frontmatter + body). The provider discovers them at session start, injects each skill's **name + description** into every system prompt, and auto-registers a **`load_skill(skillName)`** tool the model can call to fetch a skill's full body when it decides the description is relevant.

It's also the workshop's **second `AIContextProvider`** (`CompactionProvider` was the first in Step 10) — and the first one we *configure* rather than just attach with defaults.

## What you'll have at the end

```text
$ ls skills/
repo-context/

$ cat skills/repo-context/SKILL.md
---
name: repo-context
description: Conventions for working in the claude-code-with-maf workshop repo.
---
When proposing or making changes:
- One step per sitting...

$ dotnet run
you > /skills
Skills loaded from ./skills/:
  repo-context  (1,386 bytes)

you > What commit prefix should I use in this repo?
claude > I'll check the repo conventions for you.
[load_skill: skillName="repo-context"]
Based on the repo conventions, the commit prefix depends on the type of change:
- [step-NN] — workshop steps
- [doc]     — documentation-only commits
- [test]    — test scaffolding
```

The model wasn't told to use `load_skill`. The framework's `AgentSkillsProvider` injects the metadata into the system prompt, the framework also registers `load_skill` as a tool the model can call, and the model figures the rest out. **Body-on-demand is automatic.**

If `./skills/` doesn't exist, the provider isn't attached — Step 11 is purely additive.

## MAF concepts introduced

### 1. `AgentSkillsProvider` and its builder

| Type | What it does |
|---|---|
| `AgentSkillsProvider` | An `AIContextProvider`; surfaces a list of discovered/registered `AgentSkill`s to the model each turn AND auto-registers a `load_skill` tool. |
| `AgentSkillsProviderBuilder` | The fluent path the framework expects you to use. Direct ctors exist but the builder is what the docs (such as they are) point at. |
| `AgentSkillsSource` (abstract) | A source of skills. The shipped concrete one — used internally by `UseFileSkill` — discovers `<dir>/<name>/SKILL.md` files. |
| `AgentFileSkillsSourceOptions` | Per-source knobs (`ResourceDirectories`, `ScriptDirectories`, `AllowedResourceExtensions`, `AllowedScriptExtensions`). Empty defaults are fine for pure-markdown skills. |
| `AgentSkill` (abstract) | One skill: `Frontmatter` + `Content` + `Resources` + `Scripts`. |
| `AgentFileSkill` | Concrete: a skill parsed from a folder. Adds `Path`. |
| `AgentInlineSkill` | Concrete: a skill defined programmatically. Has fluent `AddResource(...)` / `AddScript(...)` for hooking C# delegates. |
| `AgentSkillFrontmatter` | The parsed frontmatter: `Name`, `Description`, `AllowedTools`, `Compatibility`, `License`, `Metadata`. |
| `AgentFileSkillScriptRunner` (delegate) | `(skill, script, params, sp, ct) → Task<object>`. Required by the builder even when no skill defines scripts. **More on this below.** |

### 2. The Claude Code Skills folder convention

The file `SKILL.md` is the manifest. The folder name is by convention the skill name (though `name:` in the frontmatter is what the framework actually uses). Resources and scripts the skill needs at runtime sit as sibling files in the same folder.

```
skills/
├── repo-context/
│   └── SKILL.md            ← discovered by the provider
├── pdf-summarizer/
│   ├── SKILL.md
│   ├── extract.py          ← script (needs a runner to actually run)
│   └── prompts/
│       └── summary.txt     ← a resource the skill can hand back
└── stray.md                ← silently IGNORED — flat files at the root don't get discovered
```

`AgentFileSkillsSource` walks `skills/*/SKILL.md`. There's no recursion beyond one level, and flat `.md` files at the root are skipped without comment. Don't ask how I learned that.

### 3. Progressive disclosure — metadata in the prompt, body via a tool

This is the design that earns the chapter. The framework does **two** things when you attach `AgentSkillsProvider`:

1. **Inject metadata into the system prompt every turn.** Each loaded skill contributes one line: `name + description`. A 50 KB skill costs the same as a 1 KB one — flat per-turn cost regardless of how much rope the body has.
2. **Register a `load_skill(skillName)` tool** the model can call when a description suggests a skill is relevant. The tool returns the body. The model now has the full skill content in its context for the rest of the conversation (or until compaction removes it).

Net effect: **cheap-by-default, rich-on-demand**. A repo with 20 skills costs ~4 KB of system prompt overhead (descriptions only) regardless of how big the bodies are. The model pays the body cost only when it decides to.

The model finds the tool the normal way — `load_skill` shows up in its tool schema with a framework-provided description, alongside `read_file`, `bash`, and the rest. We don't write the tool, we don't have to tell the model the tool exists; attaching the provider is the entire integration.

## Code walkthrough

### Wire the provider — `Agent/AgentBuilder.cs`

Three additions: a constant for the directory name, a deny-runner for skill scripts, and the builder call.

```csharp
public const string SkillsDirectoryName = "skills";
```

The builder block, inserted right after the `CompactionProvider` construction so the `AIContextProviders` list grows naturally:

```csharp
var providers = new List<AIContextProvider> { compactionProvider };
var skillsDir = Path.Combine(Directory.GetCurrentDirectory(), SkillsDirectoryName);
if (Directory.Exists(skillsDir))
{
#pragma warning disable MAAI001
    AgentFileSkillScriptRunner denyScriptRunner =
        (skill, script, _, _, _) => throw new InvalidOperationException(
            $"skill script execution is not enabled in Step 11 " +
            $"(skill={skill.Frontmatter.Name}, script={script.Name}). " +
            $"see tutorial/11-skills.md stretch for how to wire a real runner.");
    var skillsProvider = new AgentSkillsProviderBuilder()
        .UseFileSkill(skillsDir, new AgentFileSkillsSourceOptions(), denyScriptRunner)
        .UseLoggerFactory(loggerFactory)
        .Build();
#pragma warning restore MAAI001
    providers.Add(skillsProvider);
}

var options = new ChatClientAgentOptions
{
    // ...as before...
    AIContextProviders = providers,
};
```

Three deliberate choices:

- **Opt-in by directory existence.** If there's no `./skills/`, the provider isn't attached at all. Older sessions and repos that don't want skills are unaffected. Same idea as `agent.json`: present-or-absent, never a config knob.
- **Deny script runner.** The builder's runner slot is required at `Build()` time even when no skill defines a script. Passing `null!` blows up inside the builder's lambda with a NullReferenceException. We supply a runner that throws a clear error if a skill ever tries to invoke a script — louder than silently no-op, smaller than wiring real execution. (`load_skill` is the framework's own built-in tool — it does *not* route through this runner.)
- **`#pragma warning disable MAAI001` narrowly.** `AgentSkillsProviderBuilder`, `AgentFileSkillsSourceOptions`, and `AgentFileSkillScriptRunner` are all `[Experimental]`. We suppress here (not project-wide) so a future MAF rename reintroduces the warning and flags the migration.

### Add the slash command — `Harness/Commands/SlashDispatch.cs`

Diagnostic mirror of the on-disk state. The framework is the source of truth for what the model actually sees, but `/skills` lets you confirm the layout is correct *without* burning a turn.

```csharp
internal sealed class SkillsCommand : ISlashCommand
{
    public string Name => "/skills";
    public string Description => "List skills under ./skills/ (workshop convention)";

    public SlashAction Run(SlashContext ctx)
    {
        var dir = Path.Combine(Directory.GetCurrentDirectory(), AgentBuilder.SkillsDirectoryName);
        // ...prints folder name + byte count of SKILL.md...
    }
}
```

The command intentionally rescans the directory each call — if you edit a skill mid-session, `/skills` reflects the change, matching the framework's actual discovery (which also re-runs each turn, modulo `DisableCaching`).

### Add a sample skill — `skills/repo-context/SKILL.md`

```markdown
---
name: repo-context
description: Conventions for working in the claude-code-with-maf workshop repo.
---

You are operating inside a workshop-style repository that grows a Claude
Code-style console agent on top of Microsoft Agent Framework (MAF)...

When proposing or making changes:
- One step per sitting...
```

This is a real, useful skill — it captures the same conventions a `CLAUDE.md` would. It's also what the framework parses: standard YAML frontmatter (`---` delimiters), `name:` and `description:` are the fields that show up in the model's system prompt.

## Verify

```bash
dotnet build && dotnet test --filter Category=Unit    # 141 tests pass
```

Then run the chat and ask a question that only the skill body answers:

```text
$ dotnet run
you > What commit prefix should I use in this repo?
claude > I'll check the repo conventions for you.
[load_skill: skillName="repo-context"]
Based on the repo conventions...
```

The model auto-called the framework's `load_skill` tool. To confirm the tool is part of its schema:

```text
you > List every tool name available to you, one per line.
claude > read_file
list_dir
glob
grep
write_file
edit_file
bash
load_skill           ← from AgentSkillsProvider
```

The framework's own log lines also confirm the provider's involvement:

```bash
$ grep -i skill claudechat.log | tail -5
"Discovered 1 potential skills"
"Loaded skill: repo-context"
"Successfully loaded 1 skills"
"Loading skill: repo-context"          ← this one is load_skill firing
```

If you delete or rename `./skills/`, the provider isn't attached on the next run, the model loses skill awareness, `load_skill` disappears from the tool list, and `/skills` reports the folder is missing.

## Pitfalls

### The framework rejects flat `.md` files

`./skills/repo-context.md` is silently skipped. The log says `Discovered 0 potential skills` and you spend twenty minutes wondering why. The convention is `./skills/<name>/SKILL.md` — each skill is a folder. The slash command mirrors this so the diagnostic doesn't lie.

### `null` script runner blows up at `Build()`

`UseFileSkill(path, opts, null)` compiles fine — the parameter type is a delegate, so null is assignable. But the builder's internal `UseFileSkills` lambda dereferences the runner unconditionally, so `Build()` throws a NullReferenceException with a stack like:

```
at AgentSkillsProviderBuilder.<>c__DisplayClass6_0.<UseFileSkills>b__0(...)
at AgentSkillsProviderBuilder.Build()
```

Pass a real runner — even if it just throws. Production wants loud failure for unimplemented script invocations, not a silent NRE inside framework internals.

### The framework already gives the model body-access — don't write your own `read_skill` tool

Writing this chapter, I went down a confident wrong path: "the framework injects metadata only; body access is a harness concern; we need a `read_skill` tool the model can call." I built it, tests passed, the model used it. Then I asked the model to list its tools and got back:

```
read_file
list_dir
glob
grep
read_skill          ← the one I wrote
write_file
edit_file
bash
load_skill          ← the one the framework auto-registered when AgentSkillsProvider was attached
```

`AgentSkillsProvider` auto-registers a `load_skill(skillName)` tool. The model's been able to read skill bodies on demand from the moment the provider was attached. My `read_skill` was redundant; a 150-LOC tool plus tests, gone.

**The lesson, which I'd put above the framework's own docs:** before writing a tool that overlaps with a framework feature's domain, **prompt the model "list every tool available to you"** in a one-shot smoke test. Framework auto-wiring is invisible from C# but trivial to surface through the agent itself. Two minutes of probing would have saved me from shipping a duplicate.

Why is this here in the chapter rather than just deleted from git history? Because the *next* step (Step 12, `FileMemoryProvider`) and the ones after will face the same temptation. `AgentSkillsProvider` auto-registers one tool we didn't ask for; `FileMemoryProvider` and `TodoProvider` probably do the same. The first probe of every new provider is now: **"list your tools."**

### Process-global tests need a shared `[Collection]`

`SkillsCommandTests` redirects `Directory.SetCurrentDirectory(...)` in its constructor. So does `AgentConfigTests.TryLoadFromCwd_loads_when_present` (inside the test body, via `Environment.CurrentDirectory`). xUnit runs test *classes* in parallel by default, so without a shared `[Collection]` they race and one silently trashes the other.

The fix is the same one Step 7 used for Console.Out: a `[Collection("Console-shared-static")]` attribute on every class that touches a process global. The name is slightly inaccurate (the collection now covers cwd too), but it's the established serializer-of-last-resort. Resist the urge to rename it inside a feature step — that's a refactoring commit, not a Step 11 commit.

### MAF API drift bites again — this time *fictionally*

Initial reflection probe said `AgentSkillsProvider` didn't exist. **It does** — the probe only loaded `Microsoft.Agents.AI.Abstractions` (the assembly that contains the `AIAgent` type I anchored on) and missed `Microsoft.Agents.AI.dll` (the bulk-API "core" assembly).

Lesson: when reflecting on a package's surface, force-load every DLL in `lib/<tfm>/`, not just the one your anchor type happens to live in. Probe template at the bottom of [Step 0's pitfalls](00-baseline.md).

## Stretch

- **Wire skill-script execution properly.** Replace the deny runner with one that resolves the script file, exec's it, and returns stdout. Route the call through `ToolApprovalAgent` so the user sees `approve script <name>?` — analogous to how `bash` is gated.
- **Override `SkillsInstructionPrompt`** to inject skill bodies inline (eager mode) instead of relying on the `load_skill` tool (on-demand mode). Compare token costs across the same conversation in both modes. Decide which side of the trade-off fits your project.
- **Per-skill `AllowedTools` enforcement.** When a skill is "in scope," restrict the tool set. Requires inspecting which skill is "active" — which the framework doesn't expose neatly; this is a real bit of design work.
- **Walk upward from cwd for `skills/`.** Like git looking for `.git`. Useful for users who run the agent from a subdirectory.
- **Make the skills directory path config-driven** via a new `agent.json` field (`skills: { dir: "..." }`).
- **Write your own `AgentSkillsSource`** to discover skills from somewhere other than the local filesystem (HTTP, S3, an embedded resource).

## Where the seams are

- `AgentSkillsProvider` is the **second of three** MAF context-provider types this workshop covers from the framework's catalog. Step 12 (`FileMemoryProvider`) and Step 13 (`TodoProvider`) attach with the same shape: builder → `AIContextProviders` array entry → optional state in the session blob. **Each of them may also auto-register tools** — verify with the "list every tool" smoke before writing your own.
- Skills *can* register tools of their own (via `AgentSkillScript`) — that means **Step 11 has a soft seam into Step 14 (hooks/middleware)**. A future "skill-aware approval policy" would treat scripts from trusted skills differently than ad-hoc bash calls.
- The deny-runner pattern is the same one we used for ToolApprovalAgent's "always deny in plan mode" path (Step 8). Two different framework features, same workshop response: **soft constraints in the prompt, hard constraints in the runner.**

## Next

→ [Step 12 — Cross-session memory via `FileMemoryProvider`](12-memory.md) *(planned)*: persist learned facts across sessions, not just within one.
