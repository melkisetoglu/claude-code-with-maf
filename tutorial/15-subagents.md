# Step 15 — Sub-agents via `SubAgentsProvider`

> *Goal: configure a named, read-only "researcher" sub-agent the main agent can delegate to. The framework auto-registers a 6-tool delegation surface (`SubAgents_StartTask` and friends) and runs the sub-agent inside the call stack of the main one. The lesson includes its own preview-drift fix story — Step 15 took two version pins to make the framework's sub-agent execution path land.*

Sub-agents are MAF's mechanism for **named specialist delegation**: build another full `AIAgent` with its own model, instructions, and tool set, hand it to `SubAgentsProvider`, and the framework auto-registers tools the main agent uses to invoke it. The async-by-design surface (start / wait / get-results / continue / clear) lets the main agent fan out work and collect results.

This step also documents the workshop's first **hard preview-drift fix** — the kind that doesn't get solved by clever reflection or honest framing but by actually pinning the right versions. An earlier draft of this chapter shipped a "wiring is correct, framework execution is broken" story. Two csproj pins turned it green. Both shapes are now part of the lesson: the pattern of sub-agents AND the pattern of debugging multi-package version collisions.

## What you'll have at the end

```text
you > /agents
Configured sub-agents:
  researcher
    role  : read-only researcher (no mutation, no shell)
    tools : read_file, list_dir, glob, grep
    model : same as main agent

The main agent delegates via SubAgents_StartTask(agentName, input).

you > Use the researcher to find out how many milestones the workshop has.
claude > I'll delegate this to the researcher sub-agent.
[SubAgents_StartTask: agentName="researcher", ...]
  → 5ms
[SubAgents_WaitForFirstCompletion: taskIds="[1]"]
  → 1440ms                          ← researcher actually executing
[SubAgents_GetTaskResults: taskId="1"]
  → 1ms
The workshop has 7 milestones (0 through 6): ...
```

The middle line — **1440ms on `WaitForFirstCompletion`** — is real wall-clock time the sub-agent took to read the file and produce its response. Sub-agent delegation now executes; the main agent picks up the result and composes its answer.

## MAF concepts introduced

### 1. `SubAgentsProvider` — pre-built sub-agents as named specialists

```csharp
new SubAgentsProvider(
    IEnumerable<AIAgent> subAgents,
    SubAgentsProviderOptions options)
```

Each sub-agent is a **full `AIAgent`** — its own model, instructions, tool set, optionally its own providers. The main agent picks one by name and delegates a task. Output flows back as text the main agent then incorporates.

| Property of `SubAgentsProviderOptions` | What it does |
|---|---|
| `Instructions` | Injected into the **main** agent's system prompt — describes when to delegate. |
| `AgentListBuilder` | `Func<…>` to customise how the available sub-agent list appears in the prompt. Default fine. |

The framework rejects empty agent lists at construction time:

```text
System.ArgumentException: At least one sub-agent must be provided. (Parameter 'agents')
```

Pinned by a test (`SubAgentsRegistrationTests.Empty_agent_list_is_rejected_by_the_framework`) so we'd notice a future contract change.

### 2. `SubTaskInfo` and `SubTaskStatus` — the task model

| Field | Type | Meaning |
|---|---|---|
| `Id` | int | Framework-assigned |
| `AgentName` | string | Which sub-agent |
| `Description` | string | The task description |
| `Status` | `SubTaskStatus` | `Running` / `Completed` / `Failed` / `Lost` |
| `ResultText` | string | Output text when `Completed` |
| `ErrorText` | string | Diagnostic when `Failed` |

Task state persists in the session bag under `SubAgentsProvider` (and `SubAgentsProvider_Runtime` — verified live). Cross-session resume preserves the task history.

### 3. The 6-tool delegation surface

Auto-registered when `SubAgentsProvider` is attached:

| Tool | Purpose |
|---|---|
| `SubAgents_StartTask` | Kick off a delegation. **Non-blocking** — returns a task id. |
| `SubAgents_WaitForFirstCompletion` | Block until at least one in-flight task finishes. |
| `SubAgents_GetTaskResults` | Read the result of a completed task. |
| `SubAgents_GetAllTasks` | List every task regardless of status. |
| `SubAgents_ContinueTask` | Send more input to an existing sub-agent task (multi-turn). |
| `SubAgents_ClearCompletedTask` | Free the slot a completed task occupies. |

Two notable design points:

- **Async-by-design.** The model can start multiple sub-agent tasks in parallel and check their status. Not the simpler "delegate and wait" we might have expected.
- **`ContinueTask`** enables multi-turn sub-agent conversations. The main agent can build on a sub-agent's earlier response without restarting it.

## Code walkthrough

### Wire the provider — `Agent/AgentBuilder.cs`

A new constant for the researcher name:

```csharp
public const string ResearcherAgentName = "researcher";
```

Build the researcher as a regular `AIAgent` with a read-only tool subset, then attach it via `SubAgentsProvider`:

```csharp
var researcherTools = new List<AITool>
{
    AIFunctionFactory.Create(ReadFile.Read, name: "read_file"),
    AIFunctionFactory.Create(ListDir.Run,   name: "list_dir"),
    AIFunctionFactory.Create(Glob.Run,      name: "glob"),
    AIFunctionFactory.Create(Grep.Run,      name: "grep"),
};
var researcherOptions = new ChatClientAgentOptions
{
    Name = ResearcherAgentName,
    ChatOptions = new ChatOptions
    {
        ModelId      = model,
        Instructions = "You are a read-only researcher...",
        Tools        = researcherTools,
    },
};
var researcher = client.AsAIAgent(researcherOptions);

#pragma warning disable MAAI001
var subAgentsProvider = new SubAgentsProvider(
    new[] { researcher },
    new SubAgentsProviderOptions
    {
        Instructions =
            "You can delegate read-only exploration to a sub-agent named " +
            "'researcher'. Use SubAgents_StartTask when you need someone to " +
            "investigate files without you having to read them yourself.",
    });
#pragma warning restore MAAI001
providers.Add(subAgentsProvider);
```

Three deliberate choices:

- **One sub-agent, not several.** The workshop teaches the *pattern*; adding a second researcher (or a "shell specialist") triples the config surface without clarifying the lesson. A second sub-agent is a stretch.
- **Researcher has zero approval-required tools.** Read-only by tool selection — no separate gate inside the sub-agent. Clean property: tool restriction *is* the safety boundary.
- **Same `AnthropicClient` for main and researcher.** One API-key allocation, one HTTP-client pool. Switching the researcher to a different model (e.g., a smaller/faster one for exploration) is a stretch.

### Two version pins in `claude-code-with-maf.csproj`

The csproj grows two new lines:

```xml
<PackageReference Include="Microsoft.Extensions.AI" Version="10.5.2" />
<PackageReference Include="Microsoft.Extensions.AI.Abstractions" Version="10.5.2" />
<PackageReference Include="Anthropic" Version="12.20.1" />
```

Why each one is load-bearing for Step 15 — see the **Pitfalls** section's preview-drift story below. The short version: without these, sub-agent execution throws `MissingMethodException` for `WebSearchToolResultContent.get_Results()`.

### Add the slash command — `/agents`

```csharp
internal sealed class AgentsCommand : ISlashCommand
{
    public string Name => "/agents";
    public string Description => "List configured sub-agents (Step 15)";

    public SlashAction Run(SlashContext ctx)
    {
        Console.WriteLine("Configured sub-agents:");
        Console.WriteLine($"  {AgentBuilder.ResearcherAgentName}");
        Console.WriteLine("    role  : read-only researcher (no mutation, no shell)");
        Console.WriteLine("    tools : read_file, list_dir, glob, grep");
        Console.WriteLine("    model : same as main agent");
        Console.WriteLine();
        Console.WriteLine("The main agent delegates via SubAgents_StartTask(agentName, input).");
    }
}
```

Static description — what we wired, not what the provider currently sees. Surfacing actual `SubTaskInfo` is a stretch (would require deserialising the framework's session-bag JSON).

## Verify

```bash
dotnet build && dotnet test --filter Category=Unit    # 160 tests pass
```

Probe the tool surface:

```text
$ dotnet run
you > List every tool name available to you, one per line.
claude > read_file
list_dir
glob
grep
write_file
edit_file
bash
load_skill
FileMemory_SaveFile
FileMemory_ReadFile
FileMemory_DeleteFile
FileMemory_ListFiles
FileMemory_SearchFiles
TodoList_Add
TodoList_Complete
TodoList_Remove
TodoList_GetRemaining
TodoList_GetAll
SubAgents_StartTask            ← from SubAgentsProvider
SubAgents_WaitForFirstCompletion
SubAgents_GetTaskResults
SubAgents_GetAllTasks
SubAgents_ContinueTask
SubAgents_ClearCompletedTask
```

Then exercise delegation end-to-end:

```text
you > Use the researcher to find out how many milestones the workshop has.
claude > I'll delegate this to the researcher sub-agent.
[SubAgents_StartTask: agentName="researcher", ...]
  → 5ms
[SubAgents_WaitForFirstCompletion: taskIds="[1]"]
  → 1440ms
[SubAgents_GetTaskResults: taskId="1"]
  → 1ms
The workshop has 7 milestones (0 through 6): ...
```

The 1440ms on `WaitForFirstCompletion` is the wall-clock time the researcher took. Step 14's tool-timing middleware sees the outer `SubAgents_*` calls; the researcher's own internal tool calls are NOT exposed to the main agent's middleware (they run inside the framework's call stack).

Inspect the session bag for the task record:

```bash
$ cat sessions/<id>.json | jq '.session.stateBag.SubAgentsProvider'
{
  "nextTaskId": 2,
  "tasks": [
    {
      "id": 1,
      "agentName": "researcher",
      "description": "...",
      "status": "Completed",
      "resultText": "The workshop has 7 milestones..."
    }
  ]
}
```

Cross-session resume preserves the task history under `SubAgentsProvider` + `SubAgentsProvider_Runtime` keys.

## Pitfalls

### Step 15 needed two version pins to land — the preview-drift fix story

This is the longest pitfall in the workshop because the failure mode and its diagnosis are independently instructive.

**Symptom:** without the pins, the sub-agent `Start → Wait → GetResults` flow appears to work but the task ends `Failed`. The session-bag entry contains:

```text
errorText: "Method not found: 'IList<AIContent> WebSearchToolResultContent.get_Results()'."
```

**Diagnosis path:**

1. Reflect on the loaded `Microsoft.Extensions.AI.Abstractions.dll`. Find `WebSearchToolResultContent`. Discover the property is now called `Outputs`, not `Results`.
2. So the framework's compiled IL is calling a renamed property. The IL came from `Microsoft.Agents.AI` 1.5.0; the runtime is loading a newer `Microsoft.Extensions.AI.Abstractions` where the rename has landed.
3. Check the dep graph: three transitive versions of `Microsoft.Extensions.AI.Abstractions` (9.5.0 / 10.4.0 / 10.5.1) all resolved. Stale code paths are reachable from multiple entry points.
4. Check `Anthropic` SDK transitive version: 12.13.0. The current SDK is at 12.20.1. The Microsoft adapter's adapter-side code that calls into the Anthropic SDK type for web-search-tool results is what triggers the rename collision.

**Fix:** two pins. The first unifies the M.E.AI version graph; the second upgrades the Anthropic SDK past the offending stale-API references.

```xml
<PackageReference Include="Microsoft.Extensions.AI" Version="10.5.2" />
<PackageReference Include="Microsoft.Extensions.AI.Abstractions" Version="10.5.2" />
<PackageReference Include="Anthropic" Version="12.20.1" />
```

**Why "stale APIs":** the Anthropic SDK's pre-12.20 versions had a type for web-search results that, when normalised through the adapter, mapped to the `Results` property on `WebSearchToolResultContent`. The 12.20+ SDKs use a cleaned-up type shape that maps to the renamed `Outputs` property. The Microsoft adapter happens to handle both transparently *once you're on 12.20+*, but the older SDK pushes it down the `Results` path. The official MAF guidance — "If using Anthropic, upgrade the provider dependency to 12.20.0 or later to remove references to stale APIs" — is exactly this.

**Workshop-level lesson:** when MAF-style preview ecosystems give you a `MissingMethodException`, the diagnosis pattern is:

1. Reflect on the loaded DLL to see what the API *currently* looks like.
2. Compare with what the framework's compiled IL expected.
3. Check the dep graph for multiple transitive versions of the offending package.
4. Look for adjacent SDK ("provider dependency") upgrades that resolve the call-site shape.

We got here in 30 minutes once we had the diagnosis path. The first 30 minutes were the chapter's earlier draft.

### Sub-agent's own work is invisible during execution

When the main agent invokes `SubAgents_StartTask`, the sub-agent runs *inside the framework's call stack*. Its tool calls (e.g., the researcher's `read_file` of TUTORIAL.md) are NOT streamed to our console — only the outer `SubAgents_*` calls show up.

Step 14's tool timing reflects this — the 1440ms `WaitForFirstCompletion` is the wall-clock for the entire sub-agent run, not per-call timing of the researcher's internal work. Want sub-agent traces? Wrap the researcher with its own `LoggingAgent` via a separate `AIAgentBuilder` chain. Stretch.

### `SubAgentsProvider` rejects empty agent lists

`new SubAgentsProvider(Array.Empty<AIAgent>(), opts)` throws `ArgumentException` at construction time. The provider can only be attached when you actually have at least one sub-agent. Pinned by a test so the contract is captured.

### Sub-agent failures still need careful handling

Step 15 ships working, but `SubTaskStatus.Failed` and `Status.Lost` are real states. The main agent's resilience comes from how *we* prompt it (its `Instructions` tell it to fall back to direct tools when delegation doesn't help). For a production setup you'd want clearer error-handling instructions ("if a sub-agent fails twice, give up and report the issue to the user") and maybe a `/agents status` slash command that surfaces recent failures.

### Process-globals tests, MAAI001 drift watch — same as always

`AgentsCommandTests` shares `[Collection("Console-shared-static")]` for Console.Out serialisation. `SubAgentsRegistrationTests` constructs a stub agent via `AnthropicClient { ApiKey = "fake" }` + `AsAIAgent(options)` — sync + offline as of this preview; if a future MAF change adds network probing at construction, tests will surface it.

`#pragma warning disable MAAI001` narrow around `SubAgentsProvider` / `SubAgentsProviderOptions` references. If a rename re-introduces friction, the warning will reappear and flag it.

## Stretch

- **A second sub-agent** — e.g., a "git specialist" with bash-only and aggressive approval gating. Demonstrates variation in role.
- **Per-sub-agent middleware.** Wrap the researcher with its own `LoggingAgent` so sub-agent traces land in `claudechat.log` alongside main-agent traces. Use `AIAgentBuilder` recursively.
- **Sub-agent with a different model.** Researcher could use Sonnet for harder analysis; main stays on Haiku for orchestration. Cost trade-off visible per-tool.
- **`/agents status`** — read the session bag, surface recent `SubTaskInfo`. Requires deserialising the framework's task JSON.
- **`agent.json` field for sub-agents** — declare them in config rather than hard-coding the researcher.
- **Parallel delegation.** Fire multiple `SubAgents_StartTask` calls in one turn; let the main agent collect results out of order via `SubAgents_WaitForFirstCompletion` + `SubAgents_GetTaskResults`. The framework supports it; we don't yet have a multi-task scenario.

## Where the seams are

- **`SubAgentsProvider` is the fifth `AIContextProvider`** (after Compaction, Skills, Memory, Todos). Attachment pattern is now mechanical: build a provider, add to `options.AIContextProviders`, framework handles state persistence under a key in the session bag.
- **Sub-agents introduce a recursive structure.** The wrap chain we built up in Steps 3, 5, 14 around the *main* agent could, in principle, be built up around each sub-agent independently. The researcher today has no wrap chain (just `client.AsAIAgent(options)`) — that's a deliberate simplification; a real "researcher with its own logging + approval gate" is a stretch.
- **The 6-tool delegation surface is a job-queue abstraction**, not a synchronous call. `StartTask` returns immediately. `WaitForFirstCompletion` polls. `ContinueTask` enables multi-turn conversations. Closer to a process model than a function-call model.
- **Preview drift can now be FIXED, not just documented.** Through Step 14 the lesson was "the framework already shipped what you're about to hand-roll." Step 15's *first draft* lesson was "the framework also breaks." Step 15's *final* lesson is "the framework also breaks, AND sometimes the right two pins fix it — the diagnosis path through reflection + dep-graph inspection + adjacent-SDK upgrades is reusable for future drift." The probe-first methodology covers the discovery half; this chapter is what the fix half looks like.

## Next

→ **Step 16 — MCP server integration** *(planned)*: connect the agent to external tools exposed via the Model Context Protocol. The probe-first rule applies — find out what MAF's MCP support actually wires before designing the harness.
