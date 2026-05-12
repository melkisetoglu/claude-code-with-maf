# Step 10 — Context compaction via `CompactionProvider`

> *Goal: long-running sessions don't fall off the context window. As history grows, MAF compacts older turns (drops tool results, then truncates) so the active prompt stays inside the model's input budget.*

Steps 0–9 had a quietly-fragile property: every turn shipped the **entire** conversation history to the model. For short sessions that's fine. For a "spend three hours fixing a bug" project session, the history hits the model's context window and you either lose old context, lose new turns, or start getting truncated responses — depending on whose code is doing the truncating.

Step 10 fixes this by attaching MAF's `CompactionProvider`. The provider watches the history, and when it crosses thresholds (50% of the input budget → drop tool results; 80% → truncate) it compacts in place. **The session blob you save now carries that compaction state**, so resuming a long session resumes the *compacted* form, not the original.

It's also the workshop's **first `AIContextProvider`** — the third extension shape in MAF, alongside **tools** and **delegating agents** from earlier steps.

## What you'll have at the end

For short sessions, nothing changes — compaction is dormant. For long ones:

```
you > [50 turns of code review, each pulling read_file and grep tool results]
... at some point ...
[compaction provider drops tool results from turns 1-30 since they're inside 50% of the input budget; recent turns and your prompts stay intact]
[at turn 80, also truncates to keep history below 80% of the input budget]
```

You won't typically *see* compaction happen — it's silent, transparent. The visible difference is that **input token counts on long sessions stay flat instead of growing linearly**.

## MAF concepts introduced

This step teaches **two** things, the second is the bigger lesson:

### 1. `CompactionProvider` and the strategy family

| Type | What it does |
|---|---|
| `CompactionProvider(strategy, stateKey, loggerFactory)` | Glue: an `AIContextProvider` that runs `strategy` against the chat history every turn. State persists under `stateKey` in the session's `AgentSessionStateBag`. |
| `ContextWindowCompactionStrategy(maxContext, maxOutput, ?toolEvictionThreshold=0.5, ?truncationThreshold=0.8)` | The workshop default. Two-stage, zero-LLM, model-window-aware. **Our pick.** |
| `ToolResultCompactionStrategy(trigger, ?minPreservedGroups=16)` | Drops/formats old tool results only |
| `SlidingWindowCompactionStrategy(trigger, ?minPreservedTurns=1)` | Keeps a rolling window of recent turns |
| `TruncationCompactionStrategy(trigger, ?minPreservedGroups=32)` | Naive truncation |
| `SummarizationCompactionStrategy(IChatClient, trigger, ...)` | **LLM-summarizes** old turns into a single message — extra cost, better fidelity |
| `PipelineCompactionStrategy(IEnumerable<CompactionStrategy>)` | Combine multiple — e.g. drop tool results, then summarize, then truncate |

Triggers (for the trigger-driven strategies):

```csharp
CompactionTriggers.TokensExceed(150_000)
CompactionTriggers.MessagesExceed(100)
CompactionTriggers.TurnsExceed(20)
CompactionTriggers.HasToolCalls()
CompactionTriggers.All(t1, t2)   // AND
CompactionTriggers.Any(t1, t2)   // OR
```

`ContextWindowCompactionStrategy` doesn't take a trigger — it computes its own from the configured window sizes. That's why it's the "just-do-the-right-thing" default.

### 2. The third MAF extension shape: `AIContextProvider`

The workshop is now teaching all three:

| Shape | Base | What it does | Steps |
|---|---|---|---|
| **Tools** | `AITool` / `AIFunction` | What the model *can do* | 1–4 |
| **Delegating agents** | `DelegatingAIAgent` | *Cross-cutting* behaviors around the agent (approval, logging, tracing) | 3, 5, 14 |
| **Providers** | `AIContextProvider` | Behaviors *attached* to a single agent — they inspect/modify chat history, hold state in the session, hook into `InvokingAsync` / `InvokedAsync` | **10**, 11, 12, 13, 15 |

The probe also surfaced sibling providers that Steps 11–15 will introduce:
`AgentSkillsProvider` (Step 11, CLAUDE.md auto-load), `FileMemoryProvider` (Step 12, cross-session memory), `TodoProvider` (Step 13, todo tracking), `SubAgentsProvider` (Step 15, sub-agents). Same base class, same attachment pattern — Step 10 is the template.

## Setup

No new packages. `Microsoft.Agents.AI.Compaction` is shipped inside `Microsoft.Agents.AI` which we already depend on.

The single biggest change is **how `AgentBuilder` constructs the agent.** Steps 0–9 used the shortcut overload:

```csharp
AIAgent inner = client.AsAIAgent(
    model: model,
    name: "ClaudeChat",
    instructions: instructions,
    tools: tools);
```

That overload doesn't accept `AIContextProviders`. To attach a provider, we switch to the `ChatClientAgentOptions` overload:

```csharp
var options = new ChatClientAgentOptions
{
    Name = "ClaudeChat",
    ChatOptions = new ChatOptions
    {
        ModelId      = model,
        Instructions = instructions,
        Tools        = tools,
    },
    AIContextProviders = new AIContextProvider[] { compactionProvider },
};
AIAgent inner = client.AsAIAgent(options);
```

Model, instructions, and tools migrate from named arguments into `options.ChatOptions`. The provider list goes on `options.AIContextProviders`.

## Walkthrough

### Building the compaction provider

*In [`Agent/AgentBuilder.cs`](../Agent/AgentBuilder.cs).*

```csharp
public const int CompactionContextWindowTokens = 200_000;
public const int CompactionMaxOutputTokens     = 8_000;
public const string CompactionStateKey         = "ClaudeChat.Compaction";

// ...

#pragma warning disable MAAI001
var compactionProvider = new CompactionProvider(
    new ContextWindowCompactionStrategy(
        maxContextWindowTokens: CompactionContextWindowTokens,
        maxOutputTokens:        CompactionMaxOutputTokens),
    stateKey: CompactionStateKey,
    loggerFactory: loggerFactory);
#pragma warning restore MAAI001
```

A few deliberate details:

**The window sizes are hardcoded for Claude Haiku 4.5 / Sonnet 4.6** (they share these). A production-ready implementation would table-drive this per model — `agent.json` could list a model registry, the compaction strategy is rebuilt when the model changes. For workshop scope, the constants live in `AgentBuilder`. Stretch exercise calls it out.

**The state key is a meaningful name** — `"ClaudeChat.Compaction"`. It appears in the session JSON's `stateBag`, alongside `toolApprovalState` and `InMemoryChatHistoryProvider`. **Sessions saved by old code (pre-Step-10) won't have this key**, which is fine — the provider initializes fresh state on first use.

**`#pragma warning disable MAAI001`** — both `CompactionProvider` and `ContextWindowCompactionStrategy` are marked `[Experimental]` in the MAF preview, same as `ToolApprovalAgent` from Step 3. We suppress narrowly so a future MAF rename re-introduces the warning and flags the migration.

**`loggerFactory` is shared** with `LoggingAgent` from Step 5. Compaction events log at the level we already configure (Debug by default). When compaction fires, you'll see entries like *"compacted N turns, M tokens reclaimed"* in `claudechat.log`.

### Attaching the provider

```csharp
var options = new ChatClientAgentOptions
{
    Name = "ClaudeChat",
    ChatOptions = new ChatOptions
    {
        ModelId      = model,
        Instructions = instructions,
        Tools        = tools,
    },
    AIContextProviders = new AIContextProvider[] { compactionProvider },
};
AIAgent inner = client.AsAIAgent(options);
```

The `AIContextProviders` collection takes any `AIContextProvider`. Step 10 has one entry; Steps 11–13 will add more — each provider is independent, each owns its own state key in the session bag, each gets called on every `InvokingAsync` / `InvokedAsync` boundary.

### What changed in the wrap chain

Conceptually nothing — the delegating-agent stack from Step 5 still wraps `inner` exactly as before:

```
ChatClientAgent (with CompactionProvider attached)
   ↳ LoggingAgent
     ↳ OpenTelemetryAgent (optional)
       ↳ ToolApprovalAgent
```

The provider lives *inside* `ChatClientAgent`, not as a wrapper. **Providers and delegating agents compose orthogonally.** Delegating agents intercept the call/response. Providers hook into the agent's own lifecycle and modify chat history before each call.

### What the session JSON looks like now

A session saved after one chat turn now has three stateBag entries:

```json
{
  "session": {
    "stateBag": {
      "toolApprovalState":          { /* ... */ },
      "InMemoryChatHistoryProvider":{ /* messages array */ },
      "ClaudeChat.Compaction":      { /* compaction strategy's internal state */ }
    }
  }
}
```

The `ClaudeChat.Compaction` entry carries whatever the strategy needs to know across turns — for `ContextWindowCompactionStrategy`, that's lightweight metadata about which groups have already been compacted. When you `--resume` a session, the provider reads its state back and continues exactly where it left off. **Resuming a long session resumes the compacted form, not the original.**

## Verify

```bash
dotnet build
dotnet test                 # 135 unit tests (133 prior + 2 new)
```

A short live turn (verifies the refactor didn't regress the basic flow):

```bash
dotnet run
you > reply with: ok
claude > ok
(turn: 2034 in + 4 out, $0.0021)
```

Inspect the saved session JSON to confirm the compaction state key is present:

```bash
cat sessions/<id>.json | python3 -m json.tool | head -20
# Look for "ClaudeChat.Compaction" inside "stateBag".
```

**Actually triggering compaction in a smoke test is expensive** — it requires accumulating ~100K+ tokens of history, which is several dollars on a real session. The chapter pitfalls explain how to confirm it fires if you ever need to (run a long script-driven session and watch the input-token counter on `/cost`).

## Pitfalls

### Window sizes are model-specific and hardcoded

`maxContextWindowTokens: 200_000` and `maxOutputTokens: 8_000` are right for Claude Haiku 4.5 and Sonnet 4.6. If you switch to a different model family (or Claude rolls out a 1M-token Opus), the hardcoded values are wrong. **Compaction will fire too early, or not soon enough.** A production setup either reads from a model registry (stretch) or accepts model-class-bound config. For the workshop, we accept the limitation.

### Compaction is silent

There's no console line announcing "I just compacted." If you want visibility, raise the log level (`CLAUDECHAT_LOG_LEVEL=Debug`) and watch `claudechat.log`. A future stretch is to surface compaction events in the per-turn line — *"(turn: N in + M out, $X; compacted K turns)"*.

### Compaction costs nothing extra with `ContextWindowCompactionStrategy`

The two-stage strategy we picked — drop tool results, then truncate — never calls the model. **No extra cost, no extra latency.** `SummarizationCompactionStrategy` is different: it sends old turns to the model and asks for a summary. That summary is then folded into the prompt going forward — *cheaper than the original turns*, but every compaction event has its own input/output cost. For long projects it can still net-save tokens; for short sessions it's pure overhead.

### Switching strategies mid-session can confuse the state bag

The strategy stores its state under `CompactionStateKey`. If you change strategies (e.g. from `ContextWindow` to `Summarization`) between session saves, the new strategy may not understand the old state. **The safe path: only change strategies on a fresh session** (`/clear` or new run). MAF doesn't currently surface a "incompatible state" error — it'd silently misbehave. Workshop pitfall.

### The Step 6 `agent.json` doesn't yet configure compaction

Threshold / window-size / strategy choice are all hardcoded in `AgentBuilder.cs`. A logical Step 6.5 would extend `AgentConfig` with a `compaction` section. Stretch.

### Tool results disappear silently after eviction

Once `ContextWindowCompactionStrategy` evicts a tool-result group (between 50% and 80% of input budget), the model can no longer see those results in subsequent turns. **If a later turn asks "what did `read_file` say about line 42?" referring to a turn-5 read that got evicted, the model will fabricate.** The chapter recommends pairing compaction with explicit `/cost` checks and `--resume`-conscious workflows.

### `AsAIAgent(model, instructions, name, tools)` shortcut no longer works for us

Once `AgentBuilder` switched to the `ChatClientAgentOptions` overload, the simpler shortcut goes unused. Future steps that add more providers/options will keep using the explicit form. **Don't revert** to the shortcut — it doesn't accept `AIContextProviders`.

### `[Experimental]` warnings stack

`MAAI001` is now suppressed around `ToolApprovalAgent` (Step 3), `CompactionProvider`, and `ContextWindowCompactionStrategy` (Step 10). Steps 11–15 will likely add more. **Each suppression is narrow on purpose** — if MAF promotes one of these to stable, the warning goes away naturally and we get notification when the API actually changes.

## Stretch exercises

- **Manual `/compact` command.** Reach into the `AIContextProviders` collection, find the `CompactionProvider`, force its strategy to run against the current chat history. Requires plumbing the provider through `SlashContext`.
- **`/compact-status` to show how full the input budget is.** Use the `CompactionMessageIndex` introspection (`TotalTokenCount`, `IncludedTokenCount`) to report something like *"input window: 47% (94000 / 200000)"*.
- **Per-model compaction config table.** A static `Dictionary<string, (int Context, int Output)>` keyed by model id. Look up at agent-build time.
- **`agent.json` compaction section.** Lift the hardcoded constants into `AgentConfig` with sensible defaults if absent.
- **Compaction event count in `/cost`.** Track how many times compaction fired in the session; add to the `/cost` output.
- **Swap to `SummarizationCompactionStrategy`** behind a flag and see what happens on a long session. Compare prompt-cache hit rates and output quality with vs without summarization.
- **`ChatReducerCompactionStrategy`** — used through `IChatReducer`. Worth experimenting if you want to drop messages based on custom logic (e.g. "drop any turn whose tool result exceeded 50KB").

## Where the seams are

What this step deliberately doesn't have:

- **No manual `/compact`.** Stretch.
- **No compaction-event visibility** in the chat line.
- **No per-model window config.** Hardcoded for Claude 4 family.
- **No summarization strategy.** The default is zero-LLM by design — cost predictability matters for the workshop.
- **No `agent.json` compaction section.** Hardcoded in `AgentBuilder`.
- **No "compaction fired" indicator** in `/cost`.

## Next

→ **Step 11 — Project context auto-load via `AgentSkillsProvider`** *(planned)*

Step 11 is the workshop's *second* `AIContextProvider`. The pattern from Step 10 repeats — construct provider, add to `AIContextProviders`, attach state key — but the behavior is different: `AgentSkillsProvider` discovers and auto-loads `CLAUDE.md`-style project context so the model knows your conventions without you pasting them in every session. **From Step 11 onward, the workshop is building project memory.**
