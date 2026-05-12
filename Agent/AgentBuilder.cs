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
//  Future steps wire more in here without touching Program.cs:
//    - Step 11+: AgentSkillsProvider, FileMemoryProvider, TodoProvider, …
//
//  Wrap order is inside-out: raw model → LoggingAgent → OpenTelemetryAgent →
//  ToolApprovalAgent. The principle is "outer is closer to the user":
//    - LoggingAgent logs raw model behaviour AS IT HAPPENS — including the
//      approval-response messages the gate forwards back, so the trace is
//      complete.
//    - OpenTelemetryAgent traces the same boundary. (Step 14 may move it.)
//    - ToolApprovalAgent intercepts at the user-visible boundary so the
//      gate is the last thing between the model and the world.
// =============================================================================

using System.Text.Json;
using Anthropic;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ClaudeChat.Config;
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

    public static AIAgent Build(
        string apiKey,
        AgentConfig? config,
        ILoggerFactory loggerFactory,
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

        // Step 5: log every Run* call at the model boundary.
        inner = new LoggingAgent(inner, loggerFactory.CreateLogger("ClaudeChat.Agent"));

        // Step 5 (opt-in): tracing. Spans emit to whatever TracerProvider
        // the caller wired up — for the workshop, that's the console
        // exporter set up in Program.cs when --otel is passed.
        if (enableOtel)
        {
            inner = new OpenTelemetryAgent(inner, OtelSourceName);
        }

        // Wrap once with the approval gate. Anything marked
        // ApprovalRequiredAIFunction will route through here.
        //
        // ToolApprovalAgent is marked [Experimental] (MAAI001) — the API may
        // change in future MAF previews. We suppress the diagnostic here
        // rather than project-wide because it should stay visible: when MAF
        // moves the type, we want the warning back to flag the migration.
#pragma warning disable MAAI001
        return new ToolApprovalAgent(inner, JsonSerializerOptions.Default);
#pragma warning restore MAAI001
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
}
