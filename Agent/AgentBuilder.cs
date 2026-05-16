// =============================================================================
//  AgentBuilder — the one place we assemble the AIAgent.
//
//  Step 0: AnthropicClient + AsAIAgent.
//  Step 1: + read_file tool.
//  Step 2: + list_dir, glob, grep — the read-only navigation toolset.
//  Step 3: + ToolApprovalAgent wrapper. simulate_action demo tool exercised
//          the gate; deleted in Step 4 in favour of real mutators.
//  Step 4: + write_file, edit_file, bash — the mutation tools, every one
//          marked ApprovalRequiredAIFunction. The agent stops being read-only.
//  Step 5: + LoggingAgent (always on) + OpenTelemetryAgent (opt-in via --otel).
//          Observability for the runtime, with token/cost reported per-turn
//          from UsageContent in the stream (see ChatLoop).
//  Step 6: tools, instructions, and model are now driven by AgentConfig
//          (typically loaded from agent.json). The hardcoded list is now a
//          named registry — config picks which entries to register and which
//          to wrap with ApprovalRequiredAIFunction.
//  Step 10: + CompactionProvider — the workshop's first AIContextProvider.
//          Construction switched from the AsAIAgent(model, instructions, …)
//          shortcut to AsAIAgent(ChatClientAgentOptions) so we can pass an
//          AIContextProviders list. Model/instructions/tools migrated to
//          options.ChatOptions (ModelId / Instructions / Tools).
//  Step 11: + AgentSkillsProvider — the workshop's second AIContextProvider,
//          configured (not written). Builder-driven. Scans ./skills/*.md for
//          YAML-frontmatter skill files, surfaces them to the model. Opt-in:
//          if ./skills/ doesn't exist we don't wire the provider at all,
//          so existing sessions are unaffected.
//  Step 12: + FileMemoryProvider — third AIContextProvider. Persistent
//          scratchpad backed by FileSystemAgentFileStore rooted at ./memory/.
//          Opt-in by directory existence. Same global-folder model as
//          skills: every session sees the same WorkingFolder, so notes the
//          model writes survive across sessions. Live probe confirmed the
//          provider auto-registers file tools — no harness wiring needed.
//  Step 13: + TodoProvider — fourth AIContextProvider. Always-on (no
//          disk state, no setup). Auto-registers 5 tools (TodoList_Add,
//          _Complete, _Remove, _GetRemaining, _GetAll). Build() return
//          shape changes from AIAgent to a small record so the harness
//          can call provider.GetAllTodosAsync(session) for /todos —
//          first provider whose read-side API matters to the harness.
//  Step 15: + SubAgentsProvider — fifth AIContextProvider. Configures
//          one read-only "researcher" sub-agent the main agent can
//          delegate to. Auto-registers 6 tools (SubAgents_StartTask /
//          _WaitForFirstCompletion / _GetTaskResults / _GetAllTasks /
//          _ContinueTask / _ClearCompletedTask). Async-by-design: the
//          model starts tasks, waits, and reads results. Required two
//          version pins in the csproj (Microsoft.Extensions.AI[.Abstractions]
//          @ 10.5.1 + Anthropic SDK @ 12.20.1) to remove stale-API
//          references — see csproj comment for the trail.
//  Step 17: + Microsoft.AgentGovernance via .WithGovernance(adapter).
//          The workshop's capstone — instead of hand-rolling a budget
//          enforcer, we attach AGT (Microsoft's production governance
//          toolkit, sibling repo at github.com/microsoft/agent-governance-toolkit).
//          One line on the AIAgentBuilder hooks AGT into the same
//          middleware chain as our existing Logging/OTel/Approval/
//          Timing pieces. Workshop reader graduates with hands-on
//          experience of the production layer covering OWASP Agentic
//          Top 10 — far beyond what Step 17's original budget-only
//          plan would have shipped. Opt-in by ./policies/default.yaml
//          existence (matches skills/, memory/ pattern). Audit trail
//          captured in BuildResult so /governance can surface it.
//  Step 16: + MCP server tools via the ModelContextProtocol 1.3.0 SDK.
//          MAF ships HostedMcpServerTool (for Anthropic-hosted MCP) but
//          its translation isn't wired through our preview Anthropic
//          adapter (verified live — tool never reached the model). We
//          go direct via the SDK: for each agent.json mcpServers entry,
//          we open an HttpClientTransport, create an McpClient, and
//          call ListToolsAsync() to discover the server's tools. Each
//          McpClientTool extends AIFunction directly, so we add them
//          to the tool list with zero adapter code — optionally wrapped
//          with ApprovalRequiredAIFunction when approvalMode = "always"
//          (default). That routes MCP calls through our existing Step 3
//          ToolApprovalAgent — same gate UX as write_file / bash. Live
//          smoke verified with Playwright MCP (npx @playwright/mcp port
//          8931): tools discovered, model sees them, approval gate
//          fires before each invocation.
//  Step 14: switch the wrap chain from imperative constructor calls to
//          the framework's AIAgentBuilder + .UseX() extension methods.
//          Same final behaviour, fluent shape:
//             new AIAgentBuilder(inner)
//                 .UseLogging(loggerFactory, _ => { })
//                 .UseOpenTelemetry(OtelSourceName, _ => { })   // when --otel
//                 .UseToolApproval(JsonSerializerOptions.Default)
//                 .Use(ToolTimingMiddleware())                  // NEW
//                 .Build(serviceProvider);
//          Adds tool-call timing via FunctionInvocationDelegatingAgentBuilderExtensions.Use
//          — the function-invocation middleware shape MAF was designed
//          for. Prints "  → 32ms" after each tool call so the user sees
//          per-call latency inline. The retroactive lesson: we hand-rolled
//          the wrap chain in Steps 3 and 5 because the builder pattern
//          looked over-engineered. Now that we have a NEW middleware to
//          add, the builder pays its keep: one fluent chain reads better
//          than four imperative wraps + a conditional, and any future
//          middleware drops into one line.
//  Future steps wire more in here without touching Program.cs:
//    - Step 15+: SubAgentsProvider, MCP, budgets …
//
//  Wrap order (now explicit in the .Use chain instead of buried in
//  reassignment lines): raw → Logging → OpenTelemetry → ToolApproval →
//  Tool-timing. Outermost is closest to the user:
//    - LoggingAgent logs raw model behaviour AS IT HAPPENS.
//    - OpenTelemetryAgent traces the same boundary.
//    - ToolApprovalAgent intercepts at the user-visible boundary so the
//      gate is the last thing between the model and the world.
//    - Tool-timing middleware sits at the function-invocation seam —
//      below the gate (timing approved calls only is the right semantic;
//      gating denials shouldn't show fake "0ms" success).
// =============================================================================

using System.Diagnostics;
using System.Text.Json;
using Anthropic;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using AgentGovernance;
using AgentGovernance.Audit;
using AgentGovernance.Extensions.Microsoft.Agents;
using AgentGovernance.Policy;
using ClaudeChat.Config;
using ClaudeChat.Harness.Commands;
using ClaudeChat.Tools;

namespace ClaudeChat.Agent;

public static class AgentBuilder
{
    public const string OtelSourceName = "ClaudeChat";
    public const string DefaultModel = "claude-haiku-4-5";

    // Step 10: ContextWindowCompactionStrategy needs to know the model's
    // input/output budgets so it can compute when to start compacting.
    // Hardcoded for Claude Haiku 4.5 / Sonnet 4.6 (they share these). A
    // production setup would table-drive this per model (stretch in the
    // chapter). Crossing 80% triggers truncation; 50% triggers tool-result
    // eviction; both are the strategy's defaults.
    public const int CompactionContextWindowTokens = 200_000;
    public const int CompactionMaxOutputTokens     = 8_000;
    public const string CompactionStateKey         = "ClaudeChat.Compaction";

    // Step 11: where AgentSkillsProvider looks for skill files. Relative to
    // cwd, matching how agent.json is discovered. Each *.md under this dir
    // is parsed as an AgentFileSkill (YAML frontmatter + body). If the dir
    // doesn't exist, the provider isn't wired — explicit opt-in.
    public const string SkillsDirectoryName = "skills";

    // Step 12: where FileMemoryProvider keeps its scratchpad files. Same
    // opt-in convention as skills/ — present-means-on. Gitignored: the
    // model writes user-specific notes here.
    public const string MemoryDirectoryName = "memory";

    // Step 15: the one sub-agent we configure (a read-only researcher).
    // Constant so the slash command and tests can refer to the same name
    // without string-typing it.
    public const string ResearcherAgentName = "researcher";

    // Step 17: Microsoft.AgentGovernance opt-in directory. If
    // ./policies/default.yaml exists at startup, the GovernanceKernel
    // is constructed against it and attached via .WithGovernance(adapter).
    // Absent = no governance attached; the agent behaves as Step 16 did.
    public const string PoliciesDirectoryName = "policies";
    public const string DefaultPolicyFileName = "default.yaml";

    // Step 17: stable identity for this workshop agent in audit records.
    // Production deployments use DID (Decentralized Identifier) style
    // strings; the workshop ships a recognizable one.
    public const string GovernanceAgentId = "did:claudechat:main";

    public const string DefaultInstructions =
        "You are a helpful assistant. Keep replies concise. " +
        "Read-only navigation: read_file, list_dir, glob, grep — auto-invoked. " +
        "Mutation tools: write_file (create/overwrite), edit_file (literal " +
        "find-and-replace in an existing file), bash (run shell commands) — " +
        "every call requires explicit user approval. " +
        "Prefer the smallest tool that does the job: edit_file for targeted " +
        "changes, write_file for new files or full rewrites, bash only for " +
        "operations the file tools can't do (running tests, git, build, etc.). " +
        "Write specific, narrow shell commands — they are easier for the user " +
        "to read and approve.";

    /// <summary>
    /// Named registry of available tool factories. Step 6 lets config pick
    /// which of these to register; the rest are excluded. Adding a new tool
    /// = adding an entry here, regardless of step.
    /// </summary>
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

    /// <summary>
    /// Default approval-required set when config doesn't specify one.
    /// Matches the Step 4 baseline: every mutation tool needs approval.
    /// </summary>
    public static readonly IReadOnlySet<string> DefaultRequireApproval =
        new HashSet<string>(StringComparer.Ordinal) { "write_file", "edit_file", "bash" };

    /// <summary>
    /// What Build() hands back. Started as a bare AIAgent; Step 13 grew it
    /// to a small record because /todos needs to call TodoProvider.GetAllTodosAsync
    /// directly. New providers with harness-relevant read APIs go here too —
    /// keep the record minimal but explicit (no static caches, no service
    /// locator).
    /// </summary>
#pragma warning disable MAAI001
    public sealed record BuildResult(
        AIAgent Agent,
        TodoProvider Todos,
        // Step 17: AGT governance kernel + a shared audit trail list the
        // /governance slash command reads. Both null when no
        // ./policies/default.yaml exists (governance not attached).
        GovernanceKernel? Governance,
        IReadOnlyList<GovernanceEvent> AuditTrail);
#pragma warning restore MAAI001

    public static BuildResult Build(
        string apiKey,
        AgentConfig? config,
        ILoggerFactory loggerFactory,
        ApprovalState approvalState,
        bool enableOtel = false,
        string? modelOverride = null)
    {
        AnthropicClient client = new() { ApiKey = apiKey };

        // Resolve model: explicit override (CLI / env) wins, then config,
        // then hardcoded default.
        var model = modelOverride
            ?? config?.Model
            ?? DefaultModel;

        var instructions = config?.Instructions ?? DefaultInstructions;

        var tools = ResolveTools(config?.Tools);

        // Step 16: connect to any configured MCP servers and append their
        // tools. Sync-over-async because Build() is sync; the cost is one
        // round-trip per server at startup. Each McpClientTool extends
        // AIFunction directly, so we add them straight to the tool list —
        // but we DON'T wrap with ApprovalRequiredAIFunction (verified live:
        // that path's broken for McpClientTool — after approval the
        // framework never invokes the tool, model loops). Approval-required
        // MCP tools land in `mcpApprovalRequired` instead; the function-
        // invocation middleware below gates them with the same
        // [y/N/a=always] UX.
        //
        // A 10-second startup CTS bounds the wait; without it, a stale
        // mcpServers entry hangs Build() indefinitely (Ctrl+C isn't wired
        // until ChatLoop starts). See field note 2026-05-16-mcp-startup-timeout.
        var mcpApprovalRequired = new HashSet<string>(StringComparer.Ordinal);
        using var mcpStartupCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            AppendMcpServerToolsAsync(tools, config?.McpServers, loggerFactory,
                                      mcpApprovalRequired, mcpStartupCts.Token)
                .GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (mcpStartupCts.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "MCP server discovery timed out after 10s. Check agent.json mcpServers " +
                "— is the server actually running? Remove the entry to start without MCP.");
        }

        // Step 10: switching from the AsAIAgent(model, instructions, …)
        // shortcut to the AsAIAgent(ChatClientAgentOptions) overload because
        // that's the path that accepts AIContextProviders. Model, instructions
        // and tools all move into options.ChatOptions; the new provider list
        // gets the CompactionProvider attached.
        //
        // ContextWindowCompactionStrategy is the workshop-friendly default:
        // zero-LLM (no extra cost), model-window-aware, two-stage built in
        // (drops tool results at 50% of input budget, truncates at 80%).
        // Other strategies (Summarization, SlidingWindow, Truncation) are
        // available — see the chapter for when each fits.
        //
        // CompactionProvider + ContextWindowCompactionStrategy are
        // [Experimental] (MAAI001) like ToolApprovalAgent; suppress here
        // rather than project-wide so a MAF rename will reintroduce the
        // warning and flag the migration.
#pragma warning disable MAAI001
        var compactionProvider = new CompactionProvider(
            new ContextWindowCompactionStrategy(
                maxContextWindowTokens: CompactionContextWindowTokens,
                maxOutputTokens:        CompactionMaxOutputTokens),
            stateKey: CompactionStateKey,
            loggerFactory: loggerFactory);
#pragma warning restore MAAI001

        // Step 11: AgentSkillsProvider — built via AgentSkillsProviderBuilder
        // (the fluent path the framework expects you to use). UseFileSkill
        // takes a directory root, an AgentFileSkillsSourceOptions (passing an
        // empty options object accepts the defaults — pure markdown skills,
        // no extra resource or script directories), and an
        // AgentFileSkillScriptRunner.
        //
        // Why a non-null runner: passing null tripped Build() at the
        // builder's UseFileSkills lambda — the runner slot is required even
        // when no skill declares scripts. We supply a deny runner that
        // throws if invoked. Wiring real script execution (and routing it
        // through the approval gate) is a Step 11 stretch.
        //
        // Opt-in: only attach if ./skills/ exists. Avoids surprising older
        // sessions and keeps the "do nothing if you haven't asked for it"
        // default. The /skills slash command observes the same convention.
        //
        // 2026-05-15 — CompactionProvider temporarily DISABLED. Construction
        // left in place above for the Step 10 lesson; just not attached.
        // MAF preview bug: the provider round-trips the in-flight message
        // list through JSON serialisation on every turn, and the polymorphic
        // ToolCall reference inside ToolApprovalRequestContent doesn't
        // survive that round-trip — the call_id binding is lost. Effect:
        // after the user approves a tool, MAF can't bind the response to
        // the original call, the model re-emits the same approval request,
        // and the conversation loops. Re-attach `compactionProvider` once
        // MAF fixes the serialisation (one-token change).
        var providers = new List<AIContextProvider>();
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

        // Step 12: FileMemoryProvider — persistent scratchpad for the model.
        //
        // Architecture (verified by reflection on the restored DLLs):
        //   FileMemoryProvider(AgentFileStore, Func<AgentSession, FileMemoryState>, options)
        //
        //   - The store is the storage backend. FileSystemAgentFileStore(root)
        //     scopes everything the agent reads/writes to that root path —
        //     even if the model tries `..`, it can't escape because the
        //     store treats its argument as relative to root.
        //
        //   - The stateAccessor maps a session to its FileMemoryState.
        //     WorkingFolder is the subpath WITHIN the store the model sees
        //     as its memory. We return the same value for every session
        //     (empty string = use the entire store), giving us TRUE
        //     cross-session memory: every session reads and writes the
        //     same folder, so notes from yesterday show up today.
        //
        //   - The Instructions string is injected into the system prompt;
        //     the framework appends information about the folder itself.
        //
        // Opt-in by directory existence, same as skills/ above. The folder
        // is gitignored (.gitignore lists it) — contents are user-specific
        // notes the model wrote, not workshop source.
        var memoryDir = Path.Combine(Directory.GetCurrentDirectory(), MemoryDirectoryName);
        if (Directory.Exists(memoryDir))
        {
#pragma warning disable MAAI001
            var memoryStore = new FileSystemAgentFileStore(memoryDir);
            var memoryProvider = new FileMemoryProvider(
                memoryStore,
                _ => new FileMemoryState { WorkingFolder = "" },
                new FileMemoryProviderOptions
                {
                    Instructions =
                        "You have a persistent memory folder available across all sessions of this agent. " +
                        "Use the file tools the memory provider gives you to save facts, decisions, and " +
                        "conventions you should remember the next time you run. Memory writes are silent " +
                        "(no approval gate) — use them freely for notes; use write_file (approval-gated) " +
                        "for changes to the user's actual project files.",
                });
#pragma warning restore MAAI001
            providers.Add(memoryProvider);
        }

        // Step 13: TodoProvider — always on, no opt-in required (no disk
        // state, no setup). The framework auto-registers 5 tools the model
        // uses to manipulate the list — verified live with the "list every
        // tool" probe (Step 11's lesson, applied before designing):
        //   TodoList_Add        — create an item (Title + Description)
        //   TodoList_Complete   — flip IsComplete = true on an item id
        //   TodoList_Remove     — delete an item by id
        //   TodoList_GetAll     — read all items
        //   TodoList_GetRemaining — read incomplete items only
        //
        // TodoProvider exposes GetAllTodosAsync(session) / GetRemainingTodosAsync(session)
        // as C# methods too — that's how /todos reads the list in the
        // harness. We hold a reference to the provider in BuildResult so
        // ChatLoop can pass it into SlashContext.
        //
        // SuppressTodoListMessage is left at default (false) so the
        // framework injects the current list into the system prompt every
        // turn — that's the entire point.
#pragma warning disable MAAI001
        var todoProvider = new TodoProvider(new TodoProviderOptions
        {
            Instructions =
                "When the user asks for multi-step work, plan it as a todo list FIRST: " +
                "add an item per major step with TodoList_Add, then work through them, " +
                "calling TodoList_Complete as you finish each. " +
                "The user can see your current list at any time with /todos.",
        });
#pragma warning restore MAAI001
        providers.Add(todoProvider);

        // Step 15: SubAgentsProvider — fifth AIContextProvider. Configures
        // ONE read-only "researcher" sub-agent the main agent can delegate
        // to. The framework auto-registers 6 tools (SubAgents_StartTask,
        // _WaitForFirstCompletion, _GetTaskResults, _GetAllTasks,
        // _ContinueTask, _ClearCompletedTask) — verified live with the
        // "list every tool" probe.
        //
        // Required version pins: an earlier draft of this step had
        // sub-agent execution failing with `MissingMethodException` on
        // `WebSearchToolResultContent.get_Results()`. Two pins fixed it
        // (see csproj for the explanation):
        //   - Microsoft.Extensions.AI[.Abstractions] @ 10.5.1
        //   - Anthropic SDK @ 12.20.1
        // The wiring below is correct and now executes end-to-end.
        //
        // Researcher sub-agent: read-only navigation tools only
        // (read_file, list_dir, glob, grep). No mutation, no bash, no
        // providers attached. Same model as main; same AnthropicClient
        // (one HTTP-client pool, one API-key allocation).
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
                Instructions =
                    "You are a read-only researcher. Explore files via the tools and " +
                    "report your findings concisely as plain text. You cannot modify " +
                    "anything; only read, list, glob, grep.",
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
                    "'" + ResearcherAgentName + "'. Use SubAgents_StartTask when you " +
                    "need someone to investigate files without you having to read them " +
                    "yourself — keeps your own context cleaner. Wait for completion " +
                    "with SubAgents_WaitForFirstCompletion and read results with " +
                    "SubAgents_GetTaskResults.",
            });
#pragma warning restore MAAI001
        providers.Add(subAgentsProvider);

        var options = new ChatClientAgentOptions
        {
            Name = "ClaudeChat",
            ChatOptions = new ChatOptions
            {
                ModelId      = model,
                Instructions = instructions,
                Tools        = tools,
            },
            AIContextProviders = providers,
        };

        AIAgent inner = client.AsAIAgent(options);

        // Step 14: fluent middleware pipeline via AIAgentBuilder.
        //
        // Each .UseX(...) call wraps the inner agent with a delegating
        // agent. Build(serviceProvider) walks the chain inside-out and
        // returns the outermost wrapped agent.
        //
        // The empty ServiceProvider is for the builder's DI hook — none
        // of the .UseX wrappers here need services, but the API requires
        // one. A future step that wants DI (HTTP client, secret store,
        // etc.) can grow this into a real ServiceCollection.
        //
        // ToolApprovalAgent + the experimental MAAI001 types still live
        // in this build path — the .Use extension methods route into them
        // internally, so the suppression follows them.
        var serviceProvider = new ServiceCollection().BuildServiceProvider();

        var builder = new AIAgentBuilder(inner)
            .UseLogging(loggerFactory, _ => { });

        if (enableOtel)
        {
            builder = builder.UseOpenTelemetry(OtelSourceName, _ => { });
        }

#pragma warning disable MAAI001
        builder = builder
            .UseToolApproval(JsonSerializerOptions.Default)
            .Use((agent, fnCtx, next, ct) =>
                McpApprovalMiddleware(fnCtx, next, ct, approvalState, mcpApprovalRequired))
            .Use(ToolTimingMiddleware);
#pragma warning restore MAAI001

        // Step 17: attach Microsoft.AgentGovernance via .WithGovernance.
        // Opt-in by ./policies/default.yaml existence (same as skills/
        // and memory/). The audit trail is a shared list the slash
        // command reads; we subscribe via kernel.OnAllEvents.
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

        var wrapped = builder.Build(serviceProvider);
        return new BuildResult(wrapped, todoProvider, governanceKernel, auditTrail);
    }

    /// <summary>
    /// Step 14 — tool-call timing middleware. Runs at the function-invocation
    /// seam (every tool call routes through here, including the framework's
    /// auto-registered load_skill, FileMemory_*, TodoList_*). Prints
    /// "  → 32ms" on its own line after each call so the user sees per-tool
    /// latency inline with the existing [tool: …] stream output.
    ///
    /// Why on its own line: the [tool: …] bracket announcement is printed
    /// by the stream renderer at the moment the model emits the call. The
    /// actual function-invocation runs after the stream chunk is flushed —
    /// we can't append to a flushed console line cleanly, so we print on
    /// the next line, indented to suggest "this is the result of the call
    /// above."
    ///
    /// On error: we still print elapsed time, plus a marker so the user
    /// can see WHICH tool failed and how long it spent failing.
    /// </summary>
    private static async ValueTask<object?> ToolTimingMiddleware(
        AIAgent agent,
        FunctionInvocationContext fnCtx,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await next(fnCtx, ct);
            sw.Stop();
            Console.WriteLine($"  → {sw.ElapsedMilliseconds}ms");
            return result;
        }
        catch
        {
            sw.Stop();
            Console.WriteLine($"  → {sw.ElapsedMilliseconds}ms (failed)");
            throw;
        }
    }

    /// <summary>
    /// Lightweight introspection over the resolved tool set, for the
    /// `/tools` slash command in Step 7. Each entry is (name, requires-
    /// approval) — readers shouldn't have to reach into the AIAgent to
    /// learn what tools are available.
    /// </summary>
    public sealed record ToolInfo(string Name, bool RequiresApproval);

    public static IReadOnlyList<ToolInfo> ResolveToolInfo(ToolsConfig? tools)
    {
        var resolved = ResolveTools(tools);
        return resolved.Select(t => t switch
        {
            ApprovalRequiredAIFunction approved => new ToolInfo(((AIFunction)approved).Name, true),
            AIFunction fn                      => new ToolInfo(fn.Name, false),
            _ => throw new InvalidOperationException("unexpected tool type"),
        }).ToList();
    }

    /// <summary>
    /// Walks the config's tools.allow list (default: every registered tool)
    /// and wraps any entry in tools.requireApproval (default: write/edit/bash
    /// intersected with allow) with ApprovalRequiredAIFunction. Unknown tool
    /// names in EXPLICIT lists blow up at startup so typos die before
    /// reaching the model; the *defaulted* approval set is silently
    /// intersected with allow so a read-only profile doesn't error.
    /// </summary>
    public static List<AITool> ResolveTools(ToolsConfig? tools)
    {
        var allow = tools?.Allow is null
            ? ToolRegistry.Keys.ToList()
            : tools.Allow.ToList();

        // Validate names BEFORE constructing anything so the error message
        // is fast and complete.
        var unknownAllow = allow.Where(name => !ToolRegistry.ContainsKey(name)).ToList();
        if (unknownAllow.Count > 0)
        {
            var known = string.Join(", ", ToolRegistry.Keys.OrderBy(k => k));
            throw new InvalidOperationException(
                $"unknown tool name(s) in agent.json tools.allow: {string.Join(", ", unknownAllow)}. " +
                $"known tools: {known}.");
        }

        HashSet<string> requireApproval;
        if (tools?.RequireApproval is null)
        {
            // Defaulted: silently restrict the safe set to what's actually
            // allowed. A read-only profile shouldn't error because
            // write_file isn't in the default-approval set's intersection.
            requireApproval = new HashSet<string>(
                DefaultRequireApproval.Where(name => allow.Contains(name)),
                StringComparer.Ordinal);
        }
        else
        {
            // Explicit: strict validation. Typos and forgetting to allow a
            // tool you've named in requireApproval should fail loudly.
            requireApproval = new HashSet<string>(tools.RequireApproval, StringComparer.Ordinal);
            var notInAllow = requireApproval.Where(name => !allow.Contains(name)).ToList();
            if (notInAllow.Count > 0)
            {
                throw new InvalidOperationException(
                    $"tool name(s) in agent.json tools.requireApproval are not in tools.allow: " +
                    $"{string.Join(", ", notInAllow)}. requireApproval must be a subset of allow.");
            }
        }

        var result = new List<AITool>(allow.Count);
        foreach (var name in allow)
        {
            var fn = ToolRegistry[name]();
            result.Add(requireApproval.Contains(name)
                ? new ApprovalRequiredAIFunction(fn)
                : (AITool)fn);
        }
        return result;
    }

    /// <summary>
    /// Step 16 — for each agent.json mcpServers entry, connect to the
    /// server via the ModelContextProtocol SDK, discover its tools, and
    /// append them to the tools list. McpClientTool extends AIFunction
    /// directly so we add them straight to the list — no wrapping.
    ///
    /// When approvalMode is "always" (the default), each tool's name is
    /// added to <paramref name="approvalRequiredNames"/> so the function-
    /// invocation middleware (McpApprovalMiddleware) can gate it. We
    /// can't use ApprovalRequiredAIFunction here because the framework's
    /// approval flow doesn't invoke McpClientTool after approval —
    /// verified live (model loops; the bug is documented in the chapter).
    ///
    /// Failure to connect throws InvalidOperationException with a clear
    /// message naming the failing server.
    /// </summary>
    public static async Task AppendMcpServerToolsAsync(
        List<AITool> tools,
        IReadOnlyList<McpServerConfig>? servers,
        ILoggerFactory loggerFactory,
        HashSet<string> approvalRequiredNames,
        CancellationToken cancellationToken)
    {
        if (servers is null || servers.Count == 0) return;

        foreach (var s in servers)
        {
            var (clientTools, _) = await ConnectAndListToolsAsync(s, loggerFactory, cancellationToken);

            var requireApproval = NormalisedMcpApprovalMode(s) == "always";

            // Optional allowedTools filter — clip the server's tool list
            // to a subset declared in agent.json.
            IEnumerable<McpClientTool> filtered = clientTools;
            if (s.AllowedTools is { Count: > 0 })
            {
                var allow = new HashSet<string>(s.AllowedTools, StringComparer.Ordinal);
                filtered = clientTools.Where(t => allow.Contains(t.Name));
            }

            foreach (var t in filtered)
            {
                tools.Add(t);
                if (requireApproval)
                    approvalRequiredNames.Add(t.Name);
            }
        }
    }

    /// <summary>
    /// Step 16 — function-invocation middleware that gates MCP tools.
    /// Sits at the same seam as ToolTimingMiddleware (Step 14) and
    /// composes with the existing ApprovalState (PlanMode beats YoloMode
    /// beats AlwaysApprove, same precedence as Step 8).
    ///
    /// Why this instead of ApprovalRequiredAIFunction: that path's
    /// approval response handler doesn't invoke McpClientTool after
    /// approval (preview gap — verified live during Step 16). Function-
    /// invocation middleware gives us direct control over `next(...)`
    /// so we can invoke the tool ourselves once approved.
    /// </summary>
    private static async ValueTask<object?> McpApprovalMiddleware(
        FunctionInvocationContext fnCtx,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken ct,
        ApprovalState approvalState,
        HashSet<string> approvalRequiredNames)
    {
        var toolName = fnCtx.Function.Name;
        if (!approvalRequiredNames.Contains(toolName))
            return await next(fnCtx, ct);

        // Plan mode wins — read-only stance for any approval-required tool.
        if (approvalState.PlanMode)
        {
            Console.WriteLine($"  approve {toolName}? [denied: in plan mode]");
            return "[denied: in plan mode — exit plan mode (/plan) and try again]";
        }

        // Yolo bypasses everything.
        if (approvalState.YoloMode)
        {
            Console.WriteLine($"  approve {toolName}? [auto-approved (yolo)]");
            return await next(fnCtx, ct);
        }

        // Per-tool "always" memory.
        if (approvalState.AlwaysApprove.Contains(toolName))
        {
            Console.WriteLine($"  approve {toolName}? [auto-approved (always)]");
            return await next(fnCtx, ct);
        }

        // Interactive prompt. Same shape as the workshop's existing
        // ApprovalPrompt.Ask — but synchronous (we're in the
        // function-invocation seam, blocking is fine).
        Console.Write($"  approve {toolName}? [y/N/a=always]: ");
        var answer = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
        switch (answer)
        {
            case "y":
            case "yes":
                Console.WriteLine();
                return await next(fnCtx, ct);
            case "a":
            case "always":
                approvalState.AlwaysApprove.Add(toolName);
                Console.WriteLine();
                return await next(fnCtx, ct);
            default:
                Console.WriteLine("  → denied");
                return $"[user denied this tool call: {toolName}]";
        }
    }

    private static string NormalisedMcpApprovalMode(McpServerConfig s)
    {
        var mode = (s.ApprovalMode ?? "always").Trim().ToLowerInvariant();
        return mode switch
        {
            "always" or "never" => mode,
            _ => throw new InvalidOperationException(
                $"agent.json mcpServers[{s.Name}].approvalMode: unknown value '{s.ApprovalMode}'. " +
                $"valid: \"always\", \"never\"."),
        };
    }

    private static async Task<(IList<McpClientTool> Tools, McpClient Client)> ConnectAndListToolsAsync(
        McpServerConfig s,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var endpoint = ParseAddress(s);

        var opts = new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            Name     = s.Name,
        };
        if (s.Headers is { Count: > 0 })
        {
            opts.AdditionalHeaders = s.Headers.ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        try
        {
            var transport = new HttpClientTransport(opts, loggerFactory);
            var client    = await McpClient.CreateAsync(transport, null, loggerFactory, ct);
            var tools     = await client.ListToolsAsync(cancellationToken: ct);
            return (tools, client);
        }
        catch (OperationCanceledException)
        {
            // Caller owns timeout/cancel policy — let it distinguish a
            // startup timeout from a real connection failure.
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"failed to connect to MCP server '{s.Name}' at {s.Address}: " +
                $"{ex.GetType().Name}: {ex.Message}. " +
                $"is the server running? (e.g., `npx @playwright/mcp@latest --port 8931`)",
                ex);
        }
    }

    private static Uri ParseAddress(McpServerConfig s)
    {
        if (string.IsNullOrWhiteSpace(s.Address))
            throw new InvalidOperationException(
                $"agent.json mcpServers[{s.Name}].address is required (e.g., \"http://localhost:8931/mcp\").");
        try
        {
            return new Uri(s.Address);
        }
        catch (UriFormatException ex)
        {
            throw new InvalidOperationException(
                $"agent.json mcpServers[{s.Name}].address: '{s.Address}' is not a valid URI ({ex.Message}).",
                ex);
        }
    }
}
