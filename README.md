# claude-code-with-maf

A workshop that grows a Claude Code-style console agent on top of **Microsoft Agent Framework** in .NET, one step at a time.

> **Following the workshop?** Read **[TUTORIAL.md](TUTORIAL.md)** for the step-by-step guide. This README is just "how to run it".

Current state: streaming REPL, no tools yet, named sessions you can list and resume — Claude Code-style.

## Prerequisites

- .NET 9 SDK
- An Anthropic API key — get one at [console.anthropic.com](https://console.anthropic.com/)

## Run

```bash
export ANTHROPIC_API_KEY=sk-ant-...

dotnet run                          # new session, prints its id
dotnet run -- --continue            # resume the most recent session
dotnet run -- --resume <id-prefix>  # resume a specific one (prefix match, like git/claude)
dotnet run -- --list                # list past sessions, newest first
dotnet run -- --help                # usage
```

Short flags: `-c`, `-r`, `-l`, `-h`.

Commands inside the chat:
- `exit` / `quit` / Ctrl+D — quit (session is saved)
- `clear` — start a new session in the same process; the previous one stays saved
- `/id` — print the current session id

Each session is persisted to `./sessions/<id>.json` after every turn. The id is a short 8-hex-char tag (e.g. `a3f7c102`); prefix-match works as long as it's unambiguous — same as `git checkout abc123` or `claude --resume abc`.

### Example

```text
$ dotnet run
Started new session: a3f7c102
Model: claude-haiku-4-5. Commands: 'exit' (quit), 'clear' (new session), '/id' (show id).

you > what's a monoid?
claude > [...]

you > exit

(session saved: a3f7c102 — resume with: dotnet run -- --resume a3f7c102)

$ dotnet run -- --list
ID          Updated              Model                   Preview
a3f7c102    2026-05-09 14:32:11  claude-haiku-4-5        what's a monoid?

$ dotnet run -- -r a3f
Resumed session a3f7c102 — what's a monoid?
```

## Configure

| Env var | Default | Notes |
|---|---|---|
| `ANTHROPIC_API_KEY` | — | Required |
| `ANTHROPIC_DEPLOYMENT_NAME` | `claude-haiku-4-5` | Try `claude-sonnet-4-6` for harder questions |

## How it works

The framework gives you the round-trip primitives — we wrap them with metadata for the listing UX.

```csharp
// Save: framework gives JsonElement, we add our own metadata around it
var snapshot = await agent.SerializeSessionAsync(session);
var record = new JsonObject {
    ["id"] = id, ["createdAt"] = ..., ["updatedAt"] = ...,
    ["model"] = model, ["preview"] = firstUserMessage,
    ["session"] = JsonNode.Parse(snapshot.GetRawText()),  // the framework blob
};
File.WriteAllText($"sessions/{id}.json", record.ToJsonString());

// Resume: pull the framework blob back out, deserialize
var obj = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
var sessionElem = obj["session"]!.Deserialize<JsonElement>();
AgentSession session = await agent.DeserializeSessionAsync(sessionElem);
```

`AsAIAgent()` is the extension from `Microsoft.Agents.AI.Anthropic` — wraps the Anthropic SDK as an `AIAgent` you can drive with the standard Agent Framework abstractions. `AgentSession` carries conversation state across turns; `Serialize/DeserializeSessionAsync` round-trip it to a `JsonElement` so you can save it anywhere (file, blob, database, Redis). The metadata wrapper (`id`, `createdAt`, `preview`, etc.) is purely so we can implement `--list` and prefix-resume — the framework doesn't track that itself.

## Files

- [claude-code-with-maf.csproj](claude-code-with-maf.csproj) — pins `Microsoft.Agents.AI.Anthropic 1.5.0-preview.260507.1`
- [Program.cs](Program.cs) — the whole app

## Notes

The package is prerelease and the MS Learn samples have drifted from the actual API. As of this commit:

| MS Learn sample | Actual API |
|---|---|
| `APIKey` | `ApiKey` |
| `AgentThread` | `AgentSession` |
| `agent.GetNewThread()` | `await agent.CreateSessionAsync()` |

Names verified by reflecting on the restored DLLs. Expect more drift on each prerelease bump.

## Next steps (not done yet)

- Add a function tool — that's where "agent framework" earns its name vs. a plain chat wrapper.
- Wire OpenTelemetry — `OpenTelemetryAgentBuilderExtensions` is already in the package.
- Try Anthropic-on-Foundry — swap `AnthropicClient` for `AnthropicFoundryClient`.
