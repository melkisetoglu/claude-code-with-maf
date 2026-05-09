# Step 00 — Baseline: streaming chat with persistent sessions

> *Goal: a working terminal chat with Claude where you can quit, come back, and pick up the conversation. Built on Microsoft Agent Framework primitives, no custom plumbing.*

This step doesn't make our app an *agent* yet — there are no tools. But it gets the boring-but-essential plumbing right: the agent abstraction, conversation state, streaming, persistence. Every later step builds on these four things, so getting them right now saves us pain.

## What you'll have at the end

```
$ dotnet run
Started new session: a3f7c102
Model: claude-haiku-4-5. Commands: 'exit' (quit), 'clear' (new session), '/id' (show id).

you > what's a monoid?
claude > A set with an associative binary operation and an identity element. [...]

you > exit
(session saved: a3f7c102 — resume with: dotnet run -- --resume a3f7c102)

$ dotnet run -- --list
ID          Updated              Model                   Preview
a3f7c102    2026-05-09 14:32:11  claude-haiku-4-5        what's a monoid?

$ dotnet run -- -r a3f
Resumed session a3f7c102 — what's a monoid?
you > and what's a group?
claude > [remembers the previous turn]
```

## MAF concepts introduced

| Concept | What it is |
|---|---|
| `AnthropicClient` | The raw Anthropic SDK client. *Not* a MAF type. |
| `AsAIAgent(...)` | Extension from `Microsoft.Agents.AI.Anthropic` that adapts the SDK client into an `AIAgent`. |
| `AIAgent` | MAF's central abstraction — provider-agnostic. From this point on we mostly stop saying "Anthropic". |
| `AgentSession` | The conversation-state object. Carries history across turns. |
| `agent.CreateSessionAsync()` | Mint a fresh, empty session. |
| `agent.RunStreamingAsync(input, session)` | Run a turn. Returns `IAsyncEnumerable<AgentResponseUpdate>`. |
| `agent.SerializeSessionAsync(session)` / `DeserializeSessionAsync(json)` | Round-trip a session to a `JsonElement` so you can persist it anywhere. |

## Setup

### 1. Create the project

```bash
dotnet new console -n claude-code-with-maf
cd claude-code-with-maf
dotnet add package Microsoft.Agents.AI.Anthropic --prerelease
```

Pin the version once you settle on one — preview packages move fast and unpinned references will break your tutorial without warning. Our [`claude-code-with-maf.csproj`](../claude-code-with-maf.csproj) pins `1.5.0-preview.260507.1`.

### 2. Set the API key

```bash
export ANTHROPIC_API_KEY=sk-ant-...
# optional: pick a model
export ANTHROPIC_DEPLOYMENT_NAME=claude-haiku-4-5
```

## Walkthrough

The full code is in [`Program.cs`](../Program.cs); this is the narrative.

### The two-line agent

```csharp
AnthropicClient client = new() { ApiKey = apiKey };
AIAgent agent = client.AsAIAgent(
    model: model,
    name: "ClaudeChat",
    instructions: "You are a helpful assistant. Keep replies concise.");
```

Two layers:
1. `AnthropicClient` is the official Anthropic SDK client (HTTP + auth).
2. `AsAIAgent(...)` is MAF's adapter: it wraps that client behind the `AIAgent` interface that everything in MAF — tools, sessions, approval gates, compaction — expects.

After this point we mostly stop touching `AnthropicClient`. The `AIAgent` is what we'll keep extending.

> **Why the indirection?** Because in three steps we'll add tools, in five we'll add a logging wrapper, in eight we'll add compaction. Every one of those is a `Microsoft.Extensions.AI` / MAF concept that knows nothing about Anthropic. Keeping the seam between "the model client" and "the agent" clean now means none of those features have to know they're talking to Claude.

### The conversation state — `AgentSession`

```csharp
AgentSession session = await agent.CreateSessionAsync();
```

A session holds the conversation: messages so far, tool-call history (later), reasoning content, etc. You pass it back into the agent on every turn.

It's deliberately opaque — you don't poke around inside it. You can:
- create one (`CreateSessionAsync`)
- run a turn against it (`RunAsync` / `RunStreamingAsync`)
- serialize it to JSON (`SerializeSessionAsync`)
- deserialize back (`DeserializeSessionAsync`)

That last pair is what makes persistence trivial: the framework hands you a `JsonElement`, you write it to a file or a blob or Redis, and on resume you hand it back.

### The streaming loop

```csharp
await foreach (var update in agent.RunStreamingAsync(input, session))
    Console.Write(update.Text);
```

`RunStreamingAsync` returns `IAsyncEnumerable<AgentResponseUpdate>`. Each update has:
- `Text` — the chunk of text generated so far (what you stream to the user)
- `MessageId`, `ResponseId`, `CreatedAt` — useful for logging
- `FinishReason` — set on the last update
- and (in later steps) **content beyond text**: tool-call requests, tool results, reasoning content

The non-streaming alternative is `RunAsync(input, session)` which gives you a single `AgentResponse` with `.Text` aggregated. We pick streaming because it makes the experience feel responsive — but more importantly, in later steps it lets us show tool calls *as they happen* rather than after the fact.

### Persistence — the only nontrivial bit

MAF gives you the round-trip primitives:

```csharp
JsonElement snapshot = await agent.SerializeSessionAsync(session);
AgentSession resumed = await agent.DeserializeSessionAsync(snapshot);
```

But it doesn't give you any session metadata — no id, no created-at, no preview. So we wrap the framework blob in our own JSON envelope:

```json
{
  "id":        "a3f7c102",
  "createdAt": "2026-05-09T10:00:00Z",
  "updatedAt": "2026-05-09T10:15:00Z",
  "model":     "claude-haiku-4-5",
  "preview":   "what's a monoid?",
  "session":   { ...framework blob, opaque to us... }
}
```

Everything but `session` is *ours*, added so we can implement `--list` and `--resume <prefix>`. The `session` field we treat as opaque and hand straight back to `DeserializeSessionAsync` on resume.

We persist after every turn, not just on exit. Costs almost nothing, and it means a Ctrl+C never loses a conversation.

### Prefix-resume — the small UX win

```csharp
dotnet run -- --resume a3f      // not a3f7c102
```

Same as `git checkout abc123` or `claude --resume abc`. We scan all session files, filter by id-prefix, and:
- 0 matches → error
- 1 match → use it
- 2+ matches → list them and exit (force the user to disambiguate)

This is harness code, not framework code — MAF doesn't know our ids exist.

## Verify

```bash
# Builds clean
dotnet build

# Help works without an API key
dotnet run -- --help

# Listing on an empty store
dotnet run -- --list
# → No sessions.

# Bad arg
dotnet run -- --resume nope
# (after API key set) → No session matches 'nope'.
```

Then with an API key set, do a real turn, exit, and resume:

```bash
dotnet run                # have a brief chat, then 'exit'
dotnet run -- -c          # should resume with "Resumed session ..."
```

## Pitfalls

### The published docs and the actual API disagree

The MS Learn samples for `Microsoft.Agents.AI.Anthropic` are stale on at least three names:

| Docs | Actual |
|---|---|
| `AnthropicClient { APIKey = ... }` | `ApiKey` (lower-case `pi`) |
| `AgentThread` | `AgentSession` |
| `agent.GetNewThread()` | `await agent.CreateSessionAsync()` |

When something doesn't compile, don't assume your code is wrong — check the actual public surface of the restored DLL.

### Reflection probe — your debugging friend

When you suspect API drift, this is the fastest way to see what's actually there. Drop a tiny console project in `/tmp/probe` with the same package reference and:

```csharp
using System.Reflection;
var asm = Assembly.LoadFrom("/path/to/Microsoft.Agents.AI.Abstractions.dll");
foreach (var t in asm.GetExportedTypes().OrderBy(t => t.FullName))
    Console.WriteLine(t.FullName);
// Then drill into specific types: t.GetMethods(), t.GetProperties()
```

You can find the restored DLL paths with `find ~/.nuget/packages/microsoft.agents.ai* -name "*.dll" -path "*net9*"`.

### Don't `--amend` the session blob

It's tempting to peek inside the framework's JSON and "fix" something. Don't. The shape will change between MAF versions, and your edits will quietly corrupt the conversation. Treat it as opaque.

### Persistence is per-cwd

We store sessions under `./sessions/`. If you `cd` into a different directory and run `dotnet run`, you'll see no past sessions. That's intentional — it makes per-project session histories possible — but it surprises people. (Step 06 will let you override the path via config.)

## Stretch exercises

- **`--delete <id>`**: prune a session by prefix.
- **`/title <name>`**: replace the auto-preview with a human title for `--list`.
- **`SESSIONS_DIR` env var**: override the storage path.
- **Pretty-print non-ASCII**: try a session in another language and confirm UTF-8 round-trips correctly through `JsonNode`. (It does, but verify.)
- **Reflection probe**: build the probe described above and dump every type in `Microsoft.Agents.AI.dll`. You'll spot many of the providers we'll use in later steps (`CompactionProvider`, `SubAgentsProvider`, `TodoProvider`, …).

## Where the seams are

Things this step *deliberately* doesn't have, that later steps add:

- No tools — the agent can only talk, not do.
- No approval — irrelevant until tools exist.
- No compaction — long sessions will eventually hit the model's context limit.
- No memory across sessions — each session is its own island.
- No CLAUDE.md — the agent has no project context.
- No slash commands — only `exit` / `clear` / `/id`.

## Next

→ **Step 01 — first read-only tool: `read_file`** *(planned)*

That step is where this becomes an agent rather than a chatbot.
