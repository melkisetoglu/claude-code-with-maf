# Step 03 — Tool-approval gate (`ToolApprovalAgent`)

> *Goal: separate "the model decided to do this" from "the action happened." A per-call human checkpoint that stands between the model and any tool we don't fully trust.*

This is the prerequisite for Step 4. Mutation tools (`write_file`, `edit_file`, `bash`) can destroy your repo, your home directory, or production. Auto-invocation means the model decides → it runs. Without a gate, a confused model that thinks it should `rm -rf node_modules` to "clean up" will do it before the next streamed token. The gate is the boundary that makes mutation safe enough to land.

It also introduces the **delegating-agent pattern** — the most important MAF idiom we haven't taught yet. Steps 5 (`LoggingAgent`), 14 (hooks/middleware) layer more delegating agents on top of this one. *Wrap an `AIAgent` to add cross-cutting behavior without changing what's inside.*

## What you'll have at the end

```
you > use simulate_action to demo deleting the build directory
claude > 
  approve simulate_action(action="delete the build directory")? [y/N]: y
  → approved

[simulate_action: action="delete the build directory"]
Done — the simulate_action tool would have deleted the build directory if
this were a real tool.

you > now demo emptying the home directory
claude > 
  approve simulate_action(action="empty the home directory")? [y/N]: n
  → denied

[simulate_action: action="empty the home directory"]
The user denied the action. simulate_action requires explicit approval; in
this case the deletion was not approved.
```

Two things to notice:

1. **The model still emits the `[simulate_action: ...]` line in both cases** — that's the *intent* trace, which is independent of whether approval went through. The result *after* the line tells you what actually happened.
2. **The read-only tools (`read_file`, `list_dir`, `glob`, `grep`) are unchanged.** Only tools wrapped in `ApprovalRequiredAIFunction` route through the gate.

## MAF concepts introduced

| Concept | What it is |
|---|---|
| **`DelegatingAIAgent`** | The base class for "agent that wraps another agent." Inherits all of `AIAgent`'s abstract methods and forwards them to the inner agent by default; overrides the ones that need the new behavior. |
| **`ToolApprovalAgent`** | A concrete `DelegatingAIAgent` (sealed). Wraps the inner agent, watches for `ApprovalRequiredAIFunction` tool calls, and emits `ToolApprovalRequestContent` instead of auto-invoking. *Marked `[Experimental]` (`MAAI001`) in the current MAF preview — see Pitfalls.* |
| **`ApprovalRequiredAIFunction`** | A wrapper around an `AIFunction` that flips its "needs approval?" flag. Registration-time decision: each tool either has it or doesn't. |
| **`ToolApprovalRequestContent`** | What you see in the stream when the model wants to call a gated tool. Carries `RequestId` and the `ToolCall` (a `FunctionCallContent`). Has a `CreateResponse(approved, reason)` helper. |
| **`ToolApprovalResponseContent`** | The user's decision, sent back as `Contents` on a follow-up `ChatMessage`. Has `Approved`, `ToolCall`, `Reason`. |
| **Multi-turn within one user input** | This is the first time a single user line requires more than one `RunStreamingAsync` call. The harness now loops: stream → collect approvals → prompt user → feed responses back as a follow-up message → stream resumes. |

## Setup

No new packages. `ToolApprovalAgent`, `ApprovalRequiredAIFunction`, and the request/response content types are already in `Microsoft.Agents.AI` / `Microsoft.Extensions.AI.Abstractions` (transitive via the Anthropic adapter).

Two new files, one throwaway:

```
Tools/SimulateAction.cs       ← demo tool (gone in Step 4)
Harness/ApprovalPrompt.cs     ← y/N UI helper
```

## Walkthrough

### The throwaway tool — `simulate_action`

*In [`Tools/SimulateAction.cs`](../Tools/SimulateAction.cs).*

```csharp
[Description("Pretend to perform a potentially-dangerous action without actually doing anything. " +
             "This tool exists in Step 3 to exercise the tool-approval gate before real mutating " +
             "tools (write_file, edit_file, bash) are introduced in Step 4. " +
             "It is approval-required: every call asks the user for explicit yes/no.")]
public static string Run(
    [Description("A short description of the action you would have performed.")]
    string action) => $"would have performed: {action}";
```

Why bother? Step 3 builds the gate. To live-test it we need *something* that triggers the gate. The real candidates — `write_file`, `bash` — belong to Step 4. So we add a deliberately fake tool whose only purpose is being approval-required. Step 4 deletes this file.

**It's labelled as fake in its own description.** The model is told this is a demo tool. That keeps the workshop honest: at end of Step 3 the gate is real but nothing it gates is real either. End of Step 4, both halves are real and the demo is gone.

### Marking it approval-required

*In [`Agent/AgentBuilder.cs`](../Agent/AgentBuilder.cs).*

```csharp
var tools = new List<AITool>
{
    AIFunctionFactory.Create(ReadFile.Read, name: "read_file"),
    AIFunctionFactory.Create(ListDir.Run,  name: "list_dir"),
    AIFunctionFactory.Create(Glob.Run,     name: "glob"),
    AIFunctionFactory.Create(Grep.Run,     name: "grep"),
    new ApprovalRequiredAIFunction(
        AIFunctionFactory.Create(SimulateAction.Run, name: "simulate_action")),
};
```

`ApprovalRequiredAIFunction(innerFunction)` is the marker. The tools list now mixes "auto-invoke" tools and "approval-required" tools — the framework treats them differently based on this wrapper. **Approval is per-tool, set at registration.** It's not a global mode and not a per-call decision the model controls.

### Wrapping with `ToolApprovalAgent`

```csharp
AIAgent inner = client.AsAIAgent(model: model, name: "ClaudeChat", instructions: ..., tools: tools);

#pragma warning disable MAAI001
return new ToolApprovalAgent(inner, JsonSerializerOptions.Default);
#pragma warning restore MAAI001
```

This is the **delegating-agent** moment. We're not changing the inner `AIAgent`. We're wrapping it with another `AIAgent` (`ToolApprovalAgent` → `DelegatingAIAgent` → `AIAgent`) that intercepts the streaming flow.

The pattern repeats in later steps:

- Step 5: `inner = new LoggingAgent(inner)` → `inner = new ToolApprovalAgent(inner, ...)`. **Order matters.** Logging on the *outside* sees the approval handshake; logging on the *inside* doesn't.
- Step 14: hooks/middleware as more delegating agents in the same chain.

`#pragma warning disable MAAI001` is intentional. `ToolApprovalAgent` is marked `[Experimental]` in the current MAF preview; the API may change. We suppress the diagnostic at the use-site rather than project-wide so when MAF moves the type, the warning will fire again to flag the migration.

`JsonSerializerOptions.Default` is what the agent uses to (de)serialize tool arguments when the approval round-trip puts them on the wire. Default options are fine for our argument shapes (strings, primitives).

### The y/N prompt

*In [`Harness/ApprovalPrompt.cs`](../Harness/ApprovalPrompt.cs).*

```csharp
public static bool Ask(ToolApprovalRequestContent request)
{
    var label = request.ToolCall is FunctionCallContent fcc ? Format(fcc) : request.ToolCall.GetType().Name;

    Console.WriteLine();
    Console.Write($"  approve {label}? [y/N]: ");
    var line = Console.ReadLine() ?? "";
    var trimmed = line.Trim();
    var approved = trimmed.Equals("y", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("yes", StringComparison.OrdinalIgnoreCase);
    Console.WriteLine(approved ? "  → approved\n" : "  → denied\n");
    return approved;
}
```

Three deliberate choices:

1. **Default-deny.** Empty input, anything that's not `y`/`yes` → denied. The asymmetry is the point: an accidentally-approved destructive action is unrecoverable; an accidentally-denied one is annoying but recoverable.
2. **No "always approve this tool" yet.** That's a session-level UX (Claude Code calls it "always allow"). It belongs with the slash-command dispatcher in Step 7. Doing it now would clutter the prompt before we've earned the right to compress it.
3. **Render the full call** (`simulate_action(action="rm -rf /")`) so the user can see *what* they're approving. A `approve simulate_action? [y/N]:` without args is unsafe — the user wouldn't know they're approving something destructive.

### The harness round-trip

*In [`Harness/ChatLoop.cs`](../Harness/ChatLoop.cs).*

This is the biggest change. In Steps 0–2, one user input meant one `RunStreamingAsync`. In Step 3, one user input may mean *several* — each approval requires another round-trip with the response message:

```csharp
ChatMessage nextMessage = new(ChatRole.User, input);
while (true)
{
    var pendingApprovals = new List<ToolApprovalRequestContent>();

    await foreach (var update in agent.RunStreamingAsync(nextMessage, session))
    {
        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case TextContent text:                  Console.Write(text.Text);                  break;
                case FunctionCallContent call:          Console.WriteLine($"\n[{FormatCall(call)}]"); break;
                case ToolApprovalRequestContent req:    pendingApprovals.Add(req);                 break;
            }
        }
    }

    if (pendingApprovals.Count == 0) break;

    var responses = new List<AIContent>();
    foreach (var req in pendingApprovals)
    {
        var approved = ApprovalPrompt.Ask(req);
        responses.Add(req.CreateResponse(approved, approved ? "user approved" : "user denied"));
    }
    nextMessage = new ChatMessage(ChatRole.User, responses);
}
```

A few mechanics worth noticing:

- **Approvals are batched per stream.** If the model wants to call three approval-required tools in one turn, you get three `ToolApprovalRequestContent` items. We collect them all, then prompt for each, then send all responses back in one follow-up message. Faster than three separate prompts and matches what the model expects.
- **The session is the same across iterations.** `RunStreamingAsync(message, session)` — the `session` carries conversation state, so the second call resumes from where the first stopped. We're not opening a new conversation; we're continuing the current one.
- **Once `pendingApprovals` is empty, the inner loop breaks.** That means either (a) the turn produced no approval-required calls, or (b) the model finished its response after the previous round of approvals.

### Why ToolApprovalAgent goes *outermost*

In the wrapping order:

```csharp
AIAgent inner = client.AsAIAgent(...);             // bottom: raw Anthropic
return new ToolApprovalAgent(inner, ...);          // top: gate
```

The gate is the *outer* layer. When Step 5 adds `LoggingAgent`, the natural order is:

```csharp
AIAgent inner = client.AsAIAgent(...);
inner = new LoggingAgent(inner);                  // logs raw model behaviour
return new ToolApprovalAgent(inner, ...);         // gates at the boundary
```

You log the *attempt*; you gate at the user-visible boundary. Reversing it (gate inside, log outside) means the log records "tool ran" but the gate already said no — confusing. **The chain reads outside-in: closest-to-user first.**

## Verify

```bash
dotnet build
dotnet test          # 44 unit tests, including 13 for ApprovalPrompt
```

Live tests (with `ANTHROPIC_API_KEY` exported or in `.env`):

```bash
dotnet run
you > use simulate_action to demo deleting the build directory
# Expect: [simulate_action: ...] line, then approval prompt, type "y"
# Expect: model relays "would have performed: ..."

you > /clear
you > use simulate_action to demo wiping the home directory
# Type "n" at the prompt
# Expect: model acknowledges denial

you > /clear
you > list the Tools directory
# Expect: NO approval prompt (read-only tool, gate doesn't apply)
```

If the third test prompts for approval, something's wrong — either you accidentally wrapped a read-only tool with `ApprovalRequiredAIFunction`, or the `ToolApprovalAgent` is intercepting more than it should.

## Pitfalls

### `[Experimental]` MAAI001 will fire on `ToolApprovalAgent`

The MAF preview marks `ToolApprovalAgent` as evaluation-only. We suppress the diagnostic at the call site. Don't suppress it project-wide — when MAF graduates the type or moves it, the warning is your migration signal.

### Approval-batched-per-stream is the contract, not "one prompt at a time"

If the model decides to call three gated tools in one turn, all three `ToolApprovalRequestContent` items arrive in a single stream. You prompt for each, then send *all* the responses back in one follow-up message. Trying to interleave (prompt 1 → respond → prompt 2 → respond) doesn't work — the agent is waiting for the full set.

### The `[name: args]` line shows *intent*, not execution

Currently a denied tool still prints its `[simulate_action: ...]` announcement, because the `FunctionCallContent` is in the stream regardless of whether approval succeeds. The reply text after the line tells the user what *actually* happened. A future polish step (Step 9) could move this to render *post-approval* so denied tools don't look like they ran. Not critical now.

### Ordering of delegating agents matters

Step 5's `LoggingAgent` will sit *between* the inner agent and `ToolApprovalAgent`. If you accidentally wrap them outside-in (gate first, then log), the log will record "tool ran" for actions the gate denied. Match the user's mental model: outside is closest to the human.

### `simulate_action` is throwaway

It will be deleted in Step 4 along with its test. Don't build anything on top of it. If you want a permanent "ask the user to confirm before this happens" hook, do it in Step 4 with `bash` and a more conservative system prompt.

### `ChatRole.User` for the approval response — feels odd, but is correct

Approval *response* content goes back on a `ChatMessage` with `ChatRole.User`. The role describes who *originated* this turn — the user did, by deciding y/N. The `ToolApprovalAgent` reads the response content out of the message and turns it into a `FunctionResultContent` (auto-approve) or denial internally. We don't manufacture tool-role messages by hand.

## Stretch exercises

- **`ALWAYS_APPROVE` env var** that auto-approves every prompt. Useful in CI; foreshadows Step 7's `/yolo`.
- **Log every approval decision to disk** (`approvals.jsonl`). Useful for post-mortems: "wait, when did I approve `bash rm -rf`?"
- **Show a richer prompt** — multi-line for `bash` commands or large `write_file` payloads. The single-line prompt becomes cramped fast once Step 4 lands.
- **`AlwaysApproveToolApprovalResponseContent`** — MAF has it built-in. Wire it up so a `y!` answer (with bang) sets "always approve this tool for this session." Compare ergonomics with Step 7's slash command.
- **Per-tool default policy.** Some teams want `read_file` gated too in production. Make the wrapper a boolean param on registration: `RegisterTool(method, requireApproval: true)`.

## Where the seams are

What this step deliberately doesn't have:

- **No real mutating tool.** `simulate_action` exists *only* to test the gate. Step 4 lands `write_file`, `edit_file`, `bash` and replaces the demo.
- **No `/yolo` bypass.** Step 7 (slash commands) introduces it.
- **No "always approve this tool" memory.** Same — Step 7.
- **No path scoping.** The gate is the safety mechanism; if you want filesystem-level guardrails too, that's policy code on top of the gate. Not yet.
- **No richer denial reason.** The model just gets `"user denied"`. Could be `"user denied: try a less destructive approach"` if we built a "deny with feedback" UX. Worth considering later — for now, simpler wins.

## Next

→ **Step 04 — mutation tools: `write_file`, `edit_file`, `bash` (gated by Step 3)** *(planned)*

That's the step where the agent stops being read-only. With Step 3's gate already in place, all we have to do is mark the new tools `ApprovalRequiredAIFunction` and the existing harness handles the rest. **The work in Step 3 was the prerequisite; Step 4 is the payoff.**
