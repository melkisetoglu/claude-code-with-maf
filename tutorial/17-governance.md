# Step 17 — Governance via Microsoft.AgentGovernance

> *Goal: instead of hand-rolling token budgets and circuit breakers, attach Microsoft's production-grade Agent Governance Toolkit (AGT) in one line on the `AIAgentBuilder` we built in Step 14. The workshop graduates with the same governance layer real production agents use.*

This is the workshop's capstone, and it earns the framing only because of the **probe-first rule** we've spent six milestones internalising. Step 17's original plan was a hand-rolled budget enforcer — pre-turn check, refuse if over cap, surface in `/cost`. About 400 LOC of workshop code. Honest, simple, useful.

Then a deeper probe found **`microsoft/agent-governance-toolkit`** — a Microsoft-signed, MIT-licensed, public-preview project that ships:

| What | How |
|---|---|
| Policy enforcement | YAML policies, four conflict-resolution strategies |
| Rate limiting | Per-tool / per-pattern rules |
| Circuit breakers | Built-in resilience |
| Tamper-proof audit logging | Every tool call evaluated, every decision recorded |
| Prompt-injection detection | Optional scanner on inputs |
| OpenTelemetry metrics | Hooks into Step 5's observability seam |
| Zero-trust identity | DID-style agent identifiers |
| OWASP Agentic Top 10 coverage | 10/10, 13,000+ tests |

…shipping a `Microsoft.AgentGovernance.Extensions.Microsoft.Agents` NuGet package that adds **one line** of MAF integration: `builder.WithGovernance(adapter)`.

The original Step 17 plan would have built a token-budget enforcer (about 0.5% of AGT's scope) and called it the capstone. The pivoted Step 17 attaches AGT and gets the whole thing.

This isn't a corner case in the workshop's discipline; it IS the workshop's discipline. **The rule the previous six steps were teaching was: probe before writing; use what ships.** Step 17 is where that lesson reaches its strongest form — even the capstone "write your own infrastructure" step turns out to be a "configure what Microsoft ships" step. The workshop ends honestly.

## What you'll have at the end

```text
$ ls policies/
default.yaml

$ cat policies/default.yaml
apiVersion: governance.toolkit/v1
version: "1.0"
name: claudechat-default
default_action: allow
rules:
  - name: rate-limit-bash
    condition: "tool_name == 'bash'"
    action: rate_limit
    limit: "5/minute"
    priority: 10

$ dotnet run
you > /governance
Governance: Microsoft.AgentGovernance attached
  agent id : did:claudechat:main
  policy   : ./policies/default.yaml
  events   : 0 total
  (no audit events yet — make a tool call to generate one)

you > Read README.md and tell me the first line.
claude > [read_file: path="README.md"]
The first line is # claude-code-with-maf.

you > /governance
Governance: Microsoft.AgentGovernance attached
  agent id : did:claudechat:main
  policy   : ./policies/default.yaml
  events   : 2 total
  recent (last 2):
    [2026-05-13T...] PolicyCheck agent=did:agentmesh:claudechat session=... policy=(none)
    [2026-05-13T...] PolicyCheck agent=did:agentmesh:claudechat session=... policy=(none)
```

Every tool call generates an audit event. The bash rate limit fires on the 6th call within a minute. If you remove `policies/default.yaml`, governance isn't attached — the agent behaves exactly as Step 16 did. Opt-in by file existence.

## MAF concepts introduced

This step **doesn't** introduce a new MAF type. The integration shape was finished in Step 14 when we adopted `AIAgentBuilder`. AGT lives at a layer above MAF and reuses the same builder.

### `Microsoft.AgentGovernance` — the kernel

```csharp
var kernel = new GovernanceKernel(new GovernanceOptions
{
    PolicyPaths = new List<string> { "policies/default.yaml" },
    ConflictStrategy = ConflictResolutionStrategy.PriorityFirstMatch,
    EnableAudit = true,
    EnableCircuitBreaker = true,
});
```

`GovernanceKernel` exposes sub-engines you'd otherwise wire individually: `PolicyEngine`, `RateLimiter`, `CircuitBreaker`, `InjectionDetector`, `SagaOrchestrator`, `SloEngine`, `Rings`, `AuditEmitter`, `Metrics`. One construction call wires them all.

### `Microsoft.AgentGovernance.Extensions.Microsoft.Agents` — the MAF hook

```csharp
var adapter = new AgentFrameworkGovernanceAdapter(kernel, new AgentFrameworkGovernanceOptions
{
    DefaultAgentId = "did:claudechat:main",
    EnableFunctionMiddleware = true,
});

builder = builder.WithGovernance(adapter);
```

`.WithGovernance(adapter)` adds AGT's policy-evaluation middleware into the same chain Step 14 set up. It composes with our existing `.UseLogging` / `.UseOpenTelemetry` / `.UseToolApproval` / `McpApprovalMiddleware` / `ToolTimingMiddleware`. Order in the chain matters — we put governance just after the workshop's own approval gate so an explicit user approval can still be denied by policy.

### Policy YAML — the rule format

```yaml
apiVersion: governance.toolkit/v1
version: "1.0"
name: claudechat-default
default_action: allow
rules:
  - name: rate-limit-bash
    condition: "tool_name == 'bash'"
    action: rate_limit
    limit: "5/minute"
    priority: 10
```

Conditions are expressions evaluated against the function-invocation context (`tool_name`, `path`, server identity, etc.). Actions: `allow`, `deny`, `require_approval`, `rate_limit`. Priorities resolve conflicts under `PriorityFirstMatch`. Higher priority wins.

The full schema is in AGT's documentation — the workshop ships the smallest useful version.

## Code walkthrough

### Wire — `Agent/AgentBuilder.cs`

Three new constants:

```csharp
public const string PoliciesDirectoryName = "policies";
public const string DefaultPolicyFileName = "default.yaml";
public const string GovernanceAgentId     = "did:claudechat:main";
```

The wiring block, inserted into the `AIAgentBuilder` chain right after the existing middleware:

```csharp
GovernanceKernel? governanceKernel = null;
var auditTrail = new List<GovernanceEvent>();
var policyPath = Path.Combine(Directory.GetCurrentDirectory(), PoliciesDirectoryName, DefaultPolicyFileName);
if (File.Exists(policyPath))
{
    governanceKernel = new GovernanceKernel(new GovernanceOptions
    {
        PolicyPaths = new List<string> { policyPath },
        ConflictStrategy = ConflictResolutionStrategy.PriorityFirstMatch,
        EnableAudit = true,
        EnableCircuitBreaker = true,
    });
    governanceKernel.OnAllEvents(evt => { lock (auditTrail) auditTrail.Add(evt); });

    var adapter = new AgentFrameworkGovernanceAdapter(governanceKernel, new AgentFrameworkGovernanceOptions
    {
        DefaultAgentId = GovernanceAgentId,
        EnableFunctionMiddleware = true,
    });
    builder = builder.WithGovernance(adapter);
}
```

Five design choices worth flagging:

- **Opt-in by file existence.** Same convention as `./skills/`, `./memory/`, `./policies/`. Absent = no governance attached. No `agent.json` field needed — the policy file IS the configuration.
- **`PriorityFirstMatch` conflict resolution.** First rule by priority that matches wins. AGT also offers `DenyOverrides`, `AllowOverrides`, `MostSpecificWins`. Priority-first is the easiest to reason about for the workshop's small rule set.
- **`EnableCircuitBreaker = true`** — for free; AGT's default thresholds are sensible. The workshop wins resilience by flipping a boolean.
- **Audit trail captured via `kernel.OnAllEvents`.** Stored in a `List<GovernanceEvent>` shared with the harness via `BuildResult`. The `/governance` slash command reads under a lock. Production setups would pipe to a SIEM, not an in-memory list — stretch.
- **`DefaultAgentId = "did:claudechat:main"`** — DID-style identifier for audit attribution. AGT internally normalises this to `did:agentmesh:claudechat` in event records (visible in `/governance` output). That's AGT's choice; the constant we ship is what shows up in any external audit ingest.

### Surface — `Harness/Commands/SlashDispatch.cs`

```csharp
internal sealed class GovernanceCommand : ISlashCommand
{
    public string Name => "/governance";
    public string Description => "Show AGT policy state + recent audit events";

    public SlashAction Run(SlashContext ctx)
    {
        if (ctx.Governance is null) { /* opt-in hint */ return Continue; }

        // Header: kernel attached, policy path, agent id, event count
        // Body: last 5 audit events via evt.ToString()
    }
}
```

Reads from `ctx.Governance` (kernel) + `ctx.AuditTrail` (the shared list). Takes a snapshot under the same lock `AgentBuilder` uses to write — single-writer multi-reader concurrency.

### Policy — `policies/default.yaml`

Workshop ships a small demonstrable policy:

```yaml
default_action: allow
rules:
  - name: rate-limit-bash      # bash is the most powerful tool; cap rate
    condition: "tool_name == 'bash'"
    action: rate_limit
    limit: "5/minute"
    priority: 10
  - name: rate-limit-browser   # MCP browser tools are heavy too
    condition: "starts_with(tool_name, 'browser_')"
    action: rate_limit
    limit: "10/minute"
    priority: 10
```

Default-allow keeps the agent runnable; the two rate-limit rules demonstrate policy enforcement firing visibly. Workshop reader can extend with `deny`-action rules to test catch-all behaviour, or swap default to `deny` for a closed-world setup.

## Verify

```bash
dotnet build && dotnet test --filter Category=Unit    # 173 tests pass
```

Live verification:

```text
$ dotnet run
you > /governance              # → "Microsoft.AgentGovernance attached"
you > Read README.md           # → tool call executes
you > /governance              # → 2 audit events listed with timestamps
```

To watch rate-limit firing: ask the model to make 6 bash calls in quick succession. The 6th should be denied by policy. (One way: `"Run 'date' six times via bash, one after another."` Model will batch them; AGT denies once the per-minute counter exceeds 5.)

To verify the opt-in:

```bash
$ mv policies/default.yaml policies/default.yaml.bak
$ dotnet run
you > /governance
(no ./policies/default.yaml — Microsoft.AgentGovernance not attached.)
create ./policies/default.yaml to opt in (see tutorial/17-governance.md).
```

## Pitfalls

### AGT is "Public Preview" — same MAAI001-style discipline applies

Versioned 3.6.0, MIT-licensed, Microsoft-signed, but explicitly *preview*. Future versions could rename internals (`GovernanceKernel` → `GovernanceContext`, etc.). We don't suppress warnings narrowly (AGT doesn't use the `[Experimental]` analyzer the way MAF does) — the build will catch type renames directly. Same vigilance: probe before assuming.

### AGT rewrites the agent ID in audit records

We pass `DefaultAgentId = "did:claudechat:main"`. Audit events show `agent=did:agentmesh:claudechat`. AGT normalises identifiers into its `did:agentmesh:` namespace; the suffix `claudechat` is derived from our ID's path part. Not a bug — AGT's choice for cross-stack identity. External audit consumers should map both forms.

### Audit trail grows unboundedly in our in-memory list

`AgentBuilder` stores audit events in `List<GovernanceEvent> auditTrail`. Long sessions accumulate. Workshop fix: `/governance` only renders the last 5. Production fix: pipe `kernel.OnAllEvents` to a durable store (file, SIEM, OpenTelemetry log emitter); don't keep in memory. AGT's tamper-proof audit logging is the right answer at scale — we're using the simplest path here for the workshop's `/governance` display.

### Concurrency: events emitted from middleware thread, slash command reads from main thread

`OnAllEvents` callbacks run on whatever thread handled the tool invocation. `/governance` runs on the main thread. We protect with a single `lock (auditTrail)`. Trivial concurrency but worth pinning — AGT doesn't guarantee event-emission threading.

### One-policy-file convention is workshop-pragmatic, not AGT-prescribed

AGT's `PolicyPaths` is a `List<string>` — you can load many policy files at once and AGT merges them. We ship one (`policies/default.yaml`) because the workshop's policy is small and a list-of-files convention adds questions readers don't yet need to answer. Production agents typically have layered policies (org / team / app).

### AGT doesn't catch upstream API errors — `ChatLoop` does

Surfaced during Step 17's verification day: after dozens of smoke-test runs across Steps 11–17, we hit Anthropic's per-minute rate limit and the workshop crashed with `Anthropic.Exceptions.AnthropicRateLimitException`. Unhandled. Process dead. Saved session intact on disk but `dotnet run` had no agent left.

The root cause was a long-standing workshop bug: `ChatLoop.RunAsync` only caught `OperationCanceledException` (Step 9's Ctrl+C handling). Any other transient API failure bubbled up and crashed the process. Step 17's fix added three catch clauses around the streaming loop:

```csharp
catch (AnthropicRateLimitException ex)   // 429 — print "wait 60s", continue
catch (AnthropicException ex)            // any other 4xx/5xx
catch (HttpRequestException ex)          // network transport
```

Why this is *not* AGT's job: **AGT operates below the model boundary** (on tool calls dispatched by the model). Anthropic's API rate limit applies to the prompt-completion request itself, which is *above* AGT's seam — by the time AGT's policy middleware is asked to evaluate a tool call, the model has already responded successfully. AGT's circuit breaker fires on *policy-evaluation* failures, not on upstream API outages.

The workshop-level distinction: **governance is about what the agent does; resilience is what the harness does when the agent can't.** Both matter; they live at different layers.

### The original Step 17 plan was the wrong answer

The workshop's lesson from Steps 11–16 was "probe before writing." Step 17's *original* plan — hand-roll a 400-LOC budget enforcer — would have violated that lesson at the workshop's capstone. We caught it because the user asked **"does AGT align with what we're trying to do?"** mid-design. The honest answer: yes, exactly. The pivot ships ~250 LOC of integration instead of ~400 LOC of in-house infrastructure, and the production-ready surface is much larger.

The workshop-level lesson: **probe broader than the immediate framework.** MAF (`Microsoft.Agents.AI.*`) was the workshop's main probe target, but governance is a separable concern Microsoft ships in a sibling project. For the next concern that comes up (deployment? identity? telemetry?), the probe radius might need to widen again.

## Stretch

- **Default-deny policy.** Flip `default_action: deny` and add explicit `allow` rules for each tool you want to permit. Closed-world safety stance — production-realistic.
- **Pipe AGT events to `claudechat.log`.** `kernel.OnAllEvents(evt => logger.LogInformation(...))` instead of an in-memory list. Composes with Step 5's logging.
- **Enable prompt-injection detection.** `EnablePromptInjectionDetection = true` plus a `DetectionConfig`. AGT's scanner flags suspicious inputs before they reach the model.
- **Per-tool require_approval rules.** Move some of Step 4's mutation gates from `ApprovalRequiredAIFunction` to AGT's `require_approval` action — declarative instead of code.
- **Execution rings.** AGT supports tiered-trust execution rings (Ring 0 = highest trust, Ring 3 = lowest). Map workshop tools to rings via `RingThresholds`.
- **SLO tracking.** AGT's `SloEngine` emits OpenTelemetry metrics for latency and error budgets. Wire to the Step 5 OTel exporter for visibility.
- **A budget-style policy.** AGT doesn't have a built-in token-spend rule type (its primitives are rate / deny / approval / ring), but you can compose: rate-limit tool invocations (cap volume) + circuit-break on errors (cap consecutive failures) gets you 80% of what a token cap buys.

## Where the seams are

- **AGT slots into Step 14's `AIAgentBuilder` chain via `.WithGovernance(...)`.** The seam exists because we adopted the builder pattern in Step 14. Without that refactor, AGT would have required a different integration path.
- **The workshop's existing approval gate stays.** AGT can require approval per policy, but the workshop's `ApprovalRequiredAIFunction(write_file)` etc. wrapping still runs. Both compose. A future cleanup would move all approval logic to AGT's declarative form.
- **MCP-tool approval (Step 16's middleware) sits beside AGT's middleware.** AGT doesn't know about our `McpApprovalMiddleware`; they're both in the `.Use` chain, both run on every tool call, and they don't conflict because each makes its own decision. The chapter's "Wrap order" framing from Step 14 still holds.
- **Audit-trail concurrency is one `lock`.** Step 7's `/cost` and Step 13's `/todos` got away with no locks because their state is mutated only by ChatLoop (single thread). Governance audit events come from AGT's internal thread pool; the lock is real.

## Where the workshop ends

**17 of 17.** Six milestones:

| Milestone | What | Status |
|---|---|---|
| 0 — Foundation | Streaming chat + named sessions | ✅ |
| 1 — Tools + safety | `read_file`/`list_dir`/`glob`/`grep`/`write_file`/`edit_file`/`bash` + approval gate | ✅ |
| 2 — Observability | Logging, OTel, per-turn token/cost | ✅ |
| 3 — Configuration | `agent.json` profiles | ✅ |
| 4 — Harness UX | Slash registry, plan mode, streaming polish, compaction | ✅ |
| 5 — Memory & workflow | Skills, persistent memory, todos | ✅ |
| 6 — Power features | Middleware pipeline, sub-agents, MCP, governance | ✅ |

The shipping agent has:

- 11 slash commands (`/help`, `/exit`, `/quit`, `/clear`, `/id`, `/model`, `/cost`, `/tools`, `/sessions`, `/yolo`, `/plan`, `/skills`, `/memory`, `/todos`, `/agents`, `/mcp`, `/governance`)
- 7 workshop tools + 1 auto-registered `load_skill` + 5 `FileMemory_*` + 5 `TodoList_*` + 6 `SubAgents_*` + N `browser_*` (when Playwright MCP is wired) = 24+ tools the model can reach
- 5 `AIContextProvider`s attached (Compaction, Skills, Memory, Todos, SubAgents)
- A fluent middleware pipeline with: Logging → OpenTelemetry → ToolApproval → MCP-approval → tool-timing → AGT-governance
- Persistent named sessions resumable with `git-checkout`-style prefix matching
- 173 unit tests + a smoke-tested live integration path
- A YAML-configured production governance layer covering OWASP Agentic Top 10

The workshop-level rule it took 17 steps to fully crystallise: **probe what's shipped at every boundary before writing your own.** Tools (Steps 11–13), middleware (Step 14), MCP (Step 16), governance (Step 17) — each step where readers expected to write custom code turned out to be configuration.

## Next

There is no next. The workshop is complete. **Use AGT in production**, watch the MAF preview for type renames, and add steps to your own fork when you discover the next boundary worth probing.
