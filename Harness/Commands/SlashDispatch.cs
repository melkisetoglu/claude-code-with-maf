// =============================================================================
//  SlashDispatch — the slash-command system (Step 7).
//
//  Step 0..6 used an inline if-chain in ChatLoop:
//      if (input == "/exit") break;
//      if (input == "/clear") { ...; continue; }
//      if (input == "/id") { ...; continue; }
//
//  That worked for three commands. Step 7 adds five more (/help, /tools,
//  /cost, /model, /sessions, /yolo), so the chain becomes a dispatcher:
//  a name → ISlashCommand registry plus a small mutable context the
//  commands can read and (in /clear's case) modify.
//
//  Design notes:
//    - The ISlashCommand interface is tiny on purpose. The framework here
//      doesn't carry weight; the commands themselves do. ~20 LOC per command.
//    - SlashContext is *mutable* — `/clear` rotates the session in place
//      rather than returning some discriminated union. Workshop-pragmatic.
//    - Unknown commands return Continue with a one-line "try /help" message.
//      No crash; typos are routine.
//
//  Future:
//    - Step 8 (plan mode) adds /plan and /accept-plan.
//    - Step 9 (streaming polish) adds /interrupt or repurposes Ctrl+C.
//    - Step 11 adds /skills — lists .md files discovered under ./skills/.
//    - Step 12 adds /memory — lists files written by FileMemoryProvider.
//    - Step 13 adds /todos — lists items from TodoProvider's in-session state.
//    - Step 15 adds /agents — lists configured sub-agents.
//    - Step 16 adds /mcp — lists configured MCP servers from agent.json.
//    - Step 17 adds /governance — shows AGT policy state + recent audit.
// =============================================================================

using System.Globalization;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using ClaudeChat.Agent;
using ClaudeChat.Config;
using ClaudeChat.Observability;
using ClaudeChat.Persistence;

namespace ClaudeChat.Harness.Commands;

public enum SlashAction { Continue, Exit }

public interface ISlashCommand
{
    string Name { get; }          // "/help"
    string Description { get; }   // one line for /help
    SlashAction Run(SlashContext ctx);
}

public sealed class SlashContext
{
    // Mutable session state (used by /clear).
    public string SessionId { get; set; } = "";
    public AgentSession Session { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public string? Preview { get; set; }

    // Read-only references.
    public AIAgent Agent { get; init; } = null!;
    public string Model { get; init; } = "";
    public AgentConfig? Config { get; init; }
    public UsageAccumulator SessionUsage { get; init; } = null!;
    public ApprovalState Approval { get; init; } = null!;

    // Step 13: read-side handle on the TodoProvider for /todos. First
    // provider whose harness UX needs to call into the provider API itself
    // — /skills and /memory mirror disk state, but todos live in the
    // session bag, so we need the provider to read them.
#pragma warning disable MAAI001
    public TodoProvider Todos { get; init; } = null!;
#pragma warning restore MAAI001

    // Step 17: governance kernel + audit trail surfaced by /governance.
    // Both null when no ./policies/default.yaml is present (governance
    // not attached). When attached, AuditTrail is shared with AgentBuilder
    // via a thread-safe-ish append pattern (single writer thread from the
    // kernel's OnAllEvents callback; reader takes a snapshot under a lock).
    public AgentGovernance.GovernanceKernel? Governance { get; init; }
    public IReadOnlyList<AgentGovernance.Audit.GovernanceEvent> AuditTrail { get; init; } =
        Array.Empty<AgentGovernance.Audit.GovernanceEvent>();
}

public sealed class SlashRegistry
{
    private readonly Dictionary<string, ISlashCommand> _commands;

    public SlashRegistry(IEnumerable<ISlashCommand> commands)
    {
        _commands = commands.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ISlashCommand> All => _commands.Values
        .OrderBy(c => c.Name, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Try to dispatch a user input as a slash command. Returns null if the
    /// input is not a slash command (caller should treat as a chat turn).
    /// Returns SlashAction.Continue/Exit otherwise.
    /// </summary>
    public SlashAction? TryDispatch(string input, SlashContext ctx)
    {
        if (string.IsNullOrEmpty(input) || input[0] != '/') return null;

        // Allow either bare "/cmd" or "/cmd arg" (args are ignored in Step 7;
        // future commands can split on space).
        var firstSpace = input.IndexOf(' ');
        var name = firstSpace < 0 ? input : input[..firstSpace];

        if (_commands.TryGetValue(name, out var cmd))
            return cmd.Run(ctx);

        Console.WriteLine($"unknown command: {name}. try /help\n");
        return SlashAction.Continue;
    }

    /// <summary>
    /// Build the default registry with every Step 7 command wired in.
    /// HelpCommand needs to read the registry it lives in — chicken-and-egg
    /// solved by capturing a lazy reference in a closure.
    /// </summary>
    public static SlashRegistry Default()
    {
        SlashRegistry? self = null;
        var commands = new List<ISlashCommand>
        {
            new ExitCommand("/exit",  "Exit chat (session is saved)"),
            new ExitCommand("/quit",  "Alias for /exit"),
            new HelpCommand(() => self!.All),
            new IdCommand(),
            new ClearCommand(),
            new ModelCommand(),
            new CostCommand(),
            new ToolsCommand(),
            new SessionsCommand(),
            new YoloCommand(),
            new PlanCommand(),
            new SkillsCommand(),
            new MemoryCommand(),
            new TodosCommand(),
            new AgentsCommand(),
            new McpCommand(),
            new GovernanceCommand(),
            new CompactCommand(),
        };
        self = new SlashRegistry(commands);
        return self;
    }
}

// ---------- commands ----------

internal sealed class ExitCommand(string name, string description) : ISlashCommand
{
    public string Name { get; } = name;
    public string Description { get; } = description;
    public SlashAction Run(SlashContext ctx) => SlashAction.Exit;
}

internal sealed class HelpCommand(Func<IReadOnlyList<ISlashCommand>> all) : ISlashCommand
{
    public string Name => "/help";
    public string Description => "Show this help";

    public SlashAction Run(SlashContext ctx)
    {
        Console.WriteLine();
        Console.WriteLine("Slash commands:");
        foreach (var cmd in all())
            Console.WriteLine($"  {cmd.Name,-12} {cmd.Description}");
        Console.WriteLine();
        return SlashAction.Continue;
    }
}

internal sealed class IdCommand : ISlashCommand
{
    public string Name => "/id";
    public string Description => "Show the current session id";
    public SlashAction Run(SlashContext ctx)
    {
        Console.WriteLine($"(session: {ctx.SessionId})\n");
        return SlashAction.Continue;
    }
}

internal sealed class ClearCommand : ISlashCommand
{
    public string Name => "/clear";
    public string Description => "Start a new session in-place (previous stays on disk)";

    public SlashAction Run(SlashContext ctx)
    {
        // Mint a new session in-place. The previous one is already on disk
        // (we persist per turn); this just rotates the in-memory state.
        ctx.SessionId = SessionStore.NewId();
        ctx.Session = ctx.Agent.CreateSessionAsync().AsTask().GetAwaiter().GetResult();
        ctx.CreatedAt = DateTime.UtcNow;
        ctx.Preview = null;
        Console.WriteLine($"(new session: {ctx.SessionId})\n");
        return SlashAction.Continue;
    }
}

internal sealed class ModelCommand : ISlashCommand
{
    public string Name => "/model";
    public string Description => "Show the current model";
    public SlashAction Run(SlashContext ctx)
    {
        Console.WriteLine($"(model: {ctx.Model})\n");
        return SlashAction.Continue;
    }
}

internal sealed class CostCommand : ISlashCommand
{
    public string Name => "/cost";
    public string Description => "Show total token use and cost for this session";
    public SlashAction Run(SlashContext ctx)
    {
        Console.WriteLine(ctx.SessionUsage.FormatSummary(ctx.Model, "session"));
        Console.WriteLine();
        return SlashAction.Continue;
    }
}

internal sealed class ToolsCommand : ISlashCommand
{
    public string Name => "/tools";
    public string Description => "List registered tools and which require approval";

    public SlashAction Run(SlashContext ctx)
    {
        var tools = AgentBuilder.ResolveToolInfo(ctx.Config?.Tools);

        Console.WriteLine();
        Console.WriteLine("Tools:");
        foreach (var t in tools.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            var marker = t.RequiresApproval ? "  [approval]" : "";
            Console.WriteLine($"  {t.Name}{marker}");
        }
        if (tools.Count == 0) Console.WriteLine("  (none)");
        Console.WriteLine();
        return SlashAction.Continue;
    }
}

internal sealed class SessionsCommand : ISlashCommand
{
    public string Name => "/sessions";
    public string Description => "List past sessions (newest first)";

    public SlashAction Run(SlashContext ctx)
    {
        var rows = SessionStore.Enumerate().OrderByDescending(r => r.UpdatedAt).ToList();
        Console.WriteLine();
        if (rows.Count == 0)
        {
            Console.WriteLine("(no sessions)\n");
            return SlashAction.Continue;
        }
        Console.WriteLine($"{"ID",-10}  {"Updated",-19}  {"Model",-22}  Preview");
        foreach (var r in rows)
            Console.WriteLine($"{r.Id,-10}  {r.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {r.Model,-22}  {r.Preview ?? "<empty>"}");
        Console.WriteLine();
        return SlashAction.Continue;
    }
}

internal sealed class YoloCommand : ISlashCommand
{
    public string Name => "/yolo";
    public string Description => "Toggle auto-approve-everything mode (off by default)";

    public SlashAction Run(SlashContext ctx)
    {
        ctx.Approval.YoloMode = !ctx.Approval.YoloMode;

        // Yolo and plan mode are mutually exclusive — enabling yolo while in
        // plan mode would defeat plan mode's whole purpose (auto-deny vs
        // auto-approve are direct opposites).
        if (ctx.Approval.YoloMode && ctx.Approval.PlanMode)
        {
            ctx.Approval.PlanMode = false;
            Console.WriteLine("(note: plan mode was on; turning it off because /yolo is incompatible.)");
        }

        Console.WriteLine(ctx.Approval.YoloMode
            ? "(yolo: ON — every approval-required tool will auto-approve. /yolo to toggle off.)\n"
            : "(yolo: OFF — approval prompts will return.)\n");
        return SlashAction.Continue;
    }
}

internal sealed class PlanCommand : ISlashCommand
{
    public string Name => "/plan";
    public string Description => "Toggle plan mode (read-only; mutation tools auto-deny)";

    public SlashAction Run(SlashContext ctx)
    {
        ctx.Approval.PlanMode = !ctx.Approval.PlanMode;

        if (ctx.Approval.PlanMode && ctx.Approval.YoloMode)
        {
            ctx.Approval.YoloMode = false;
            Console.WriteLine("(note: yolo was on; turning it off because /plan is incompatible.)");
        }

        Console.WriteLine(ctx.Approval.PlanMode
            ? "(plan: ON — read-only. Mutation tools (write_file, edit_file, bash) " +
              "will be auto-denied. Use the read tools to explore, propose a plan, " +
              "then /plan again to exit and execute.)\n"
            : "(plan: OFF — mutation tools are gated normally again.)\n");
        return SlashAction.Continue;
    }
}

internal sealed class SkillsCommand : ISlashCommand
{
    public string Name => "/skills";
    public string Description => "List skills under ./skills/ (workshop convention)";

    public SlashAction Run(SlashContext ctx)
    {
        // We rescan the directory at command time rather than caching at agent
        // build. Two reasons:
        //   (1) the user might add or edit a skill mid-session and rerun this
        //       to confirm it's there;
        //   (2) the framework's AgentSkillsProvider is the source of truth
        //       for what the model actually sees — this command is a
        //       diagnostic over the same disk state, not a mirror of the
        //       provider's internal cache.
        //
        // Layout convention (this is what AgentFileSkillsSource expects):
        //     ./skills/<skill-name>/SKILL.md
        // A flat ./skills/foo.md is silently skipped by the framework. We
        // mirror that here so the diagnostic matches what the model sees.
        var dir = Path.Combine(Directory.GetCurrentDirectory(), AgentBuilder.SkillsDirectoryName);
        Console.WriteLine();
        if (!Directory.Exists(dir))
        {
            Console.WriteLine($"(no ./{AgentBuilder.SkillsDirectoryName}/ folder — skills provider not wired.)");
            Console.WriteLine($"create ./{AgentBuilder.SkillsDirectoryName}/<name>/SKILL.md to opt in.\n");
            return SlashAction.Continue;
        }

        var skills = Directory.EnumerateDirectories(dir)
            .Select(d => new { Folder = d, Skill = Path.Combine(d, "SKILL.md") })
            .Where(x => File.Exists(x.Skill))
            .OrderBy(x => x.Folder, StringComparer.Ordinal)
            .ToList();

        if (skills.Count == 0)
        {
            Console.WriteLine($"(./{AgentBuilder.SkillsDirectoryName}/ exists but contains no <name>/SKILL.md skills.)\n");
            return SlashAction.Continue;
        }

        Console.WriteLine($"Skills loaded from ./{AgentBuilder.SkillsDirectoryName}/:");
        foreach (var s in skills)
        {
            var name = Path.GetFileName(s.Folder);
            var size = new FileInfo(s.Skill).Length;
            // Invariant culture — default formatting would use locale-specific
            // thousand separators ("1.386" on a de-DE machine), which is
            // confusing when "1.386 bytes" looks like 1.386 of a byte.
            Console.WriteLine($"  {name}  ({size.ToString("N0", CultureInfo.InvariantCulture)} bytes)");
        }
        Console.WriteLine();
        return SlashAction.Continue;
    }
}

internal sealed class MemoryCommand : ISlashCommand
{
    public string Name => "/memory";
    public string Description => "List files written by FileMemoryProvider under ./memory/";

    public SlashAction Run(SlashContext ctx)
    {
        // Same opt-in-by-existence convention as /skills. If ./memory/ is
        // absent the provider isn't wired and the command says so. If
        // present, list everything — the model writes both the actual
        // memo files AND a few framework-maintained sidecars
        // (memories.md is the index, *_description.md mirrors a tool
        // call's `description` parameter). We don't filter: showing all
        // files is more honest than pretending the framework's bookkeeping
        // doesn't exist.
        var dir = Path.Combine(Directory.GetCurrentDirectory(), AgentBuilder.MemoryDirectoryName);
        Console.WriteLine();
        if (!Directory.Exists(dir))
        {
            Console.WriteLine($"(no ./{AgentBuilder.MemoryDirectoryName}/ folder — FileMemoryProvider not wired.)");
            Console.WriteLine($"create ./{AgentBuilder.MemoryDirectoryName}/ to opt in to cross-session memory.\n");
            return SlashAction.Continue;
        }

        var files = Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        if (files.Count == 0)
        {
            Console.WriteLine($"(./{AgentBuilder.MemoryDirectoryName}/ is empty — the model hasn't written anything yet.)\n");
            return SlashAction.Continue;
        }

        Console.WriteLine($"Memory files under ./{AgentBuilder.MemoryDirectoryName}/:");
        foreach (var f in files)
        {
            var name = Path.GetFileName(f);
            var size = new FileInfo(f).Length;
            Console.WriteLine($"  {name}  ({size.ToString("N0", CultureInfo.InvariantCulture)} bytes)");
        }
        Console.WriteLine();
        return SlashAction.Continue;
    }
}

internal sealed class TodosCommand : ISlashCommand
{
    public string Name => "/todos";
    public string Description => "List the agent's todo items (✓ done, ☐ pending) from TodoProvider";

    public SlashAction Run(SlashContext ctx)
    {
        // The provider holds the list in-memory for the current session and
        // mirrors it into the session bag for persistence. We can't mirror
        // disk state the way /skills and /memory do — there is no disk
        // state. So we call into the provider directly.
        //
        // GetAllTodosAsync is async but we're in a sync ISlashCommand.Run
        // contract; the call is local-memory-only (no IO, no model round
        // trip), so a GetResult here is fine. If the framework ever moves
        // todos to async storage (vector DB, remote service), this needs
        // refactoring — flagged in the chapter.
#pragma warning disable MAAI001    // TodoItem.
        IReadOnlyList<Microsoft.Agents.AI.TodoItem> items;
#pragma warning restore MAAI001
        try
        {
            items = ctx.Todos.GetAllTodosAsync(ctx.Session).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"(error reading todos: {ex.GetType().Name}: {ex.Message})\n");
            return SlashAction.Continue;
        }

        Console.WriteLine();
        if (items.Count == 0)
        {
            Console.WriteLine("(no todos — the model hasn't planned any work in this session yet.)\n");
            return SlashAction.Continue;
        }

        var doneCount = items.Count(t => t.IsComplete);
        Console.WriteLine($"Todos ({doneCount}/{items.Count} done):");
        foreach (var t in items.OrderBy(t => t.Id))
        {
            var marker = t.IsComplete ? "✓" : "☐";
            // Title can be empty if the model only set Description; fall
            // back so the line is never just a marker.
            var label = !string.IsNullOrWhiteSpace(t.Title) ? t.Title : t.Description;
            Console.WriteLine($"  {marker} [{t.Id}] {label}");
            if (!string.IsNullOrWhiteSpace(t.Description) && t.Description != t.Title)
                Console.WriteLine($"        {t.Description}");
        }
        Console.WriteLine();
        return SlashAction.Continue;
    }
}

internal sealed class AgentsCommand : ISlashCommand
{
    public string Name => "/agents";
    public string Description => "List configured sub-agents (Step 15)";

    public SlashAction Run(SlashContext ctx)
    {
        // Static information: what we configured in AgentBuilder. We don't
        // query the SubAgentsProvider directly — it doesn't expose a
        // listing API, and the configuration is the source of truth anyway.
        //
        // If the model is mid-delegation, recent SubTaskInfo lives in the
        // session bag under "SubAgentsProvider" (verified live), but
        // surfacing that is a stretch — it would require deserialising
        // the framework's internal task-state JSON.
        Console.WriteLine();
        Console.WriteLine("Configured sub-agents:");
        Console.WriteLine($"  {AgentBuilder.ResearcherAgentName}");
        Console.WriteLine("    role  : read-only researcher (no mutation, no shell)");
        Console.WriteLine("    tools : read_file, list_dir, glob, grep");
        Console.WriteLine("    model : same as main agent");
        Console.WriteLine();
        Console.WriteLine("The main agent delegates via SubAgents_StartTask(agentName, input).");
        Console.WriteLine();
        return SlashAction.Continue;
    }
}

internal sealed class McpCommand : ISlashCommand
{
    public string Name => "/mcp";
    public string Description => "List configured MCP servers (Step 16; from agent.json mcpServers)";

    public SlashAction Run(SlashContext ctx)
    {
        // Reads agent.json directly via ctx.Config. MCP servers are
        // config-time things (URL, approval mode, headers) — we don't
        // need to query the framework's HostedMcpServerTool instances
        // for this listing; the config is the source of truth.
        //
        // Header VALUES are redacted in the output even though we have
        // them on disk — they're often bearer tokens or API keys. Show
        // only the count + key names. (Stretch: full redaction policy
        // configurable.)
        var servers = ctx.Config?.McpServers;
        Console.WriteLine();
        if (servers is null || servers.Count == 0)
        {
            Console.WriteLine("(no MCP servers configured — add an `mcpServers` entry to agent.json to opt in.)");
            Console.WriteLine("see tutorial/16-mcp.md for an example pointing at Playwright MCP.");
            Console.WriteLine();
            return SlashAction.Continue;
        }

        Console.WriteLine($"Configured MCP servers ({servers.Count}):");
        foreach (var s in servers)
        {
            Console.WriteLine($"  {s.Name}");
            Console.WriteLine($"    address      : {s.Address}");
            if (!string.IsNullOrWhiteSpace(s.Description))
                Console.WriteLine($"    description  : {s.Description}");
            Console.WriteLine($"    approvalMode : {s.ApprovalMode ?? "always (default)"}");
            if (s.AllowedTools is { Count: > 0 })
                Console.WriteLine($"    allowedTools : {string.Join(", ", s.AllowedTools)}");
            if (s.Headers is { Count: > 0 })
                Console.WriteLine($"    headers      : {s.Headers.Count} (values redacted; keys: {string.Join(", ", s.Headers.Keys)})");
        }
        Console.WriteLine();
        return SlashAction.Continue;
    }
}

internal sealed class GovernanceCommand : ISlashCommand
{
    public string Name => "/governance";
    public string Description => "Show AGT policy state + recent audit events";

    public SlashAction Run(SlashContext ctx)
    {
        Console.WriteLine();
        if (ctx.Governance is null)
        {
            Console.WriteLine($"(no ./{AgentBuilder.PoliciesDirectoryName}/{AgentBuilder.DefaultPolicyFileName} — Microsoft.AgentGovernance not attached.)");
            Console.WriteLine($"create ./{AgentBuilder.PoliciesDirectoryName}/{AgentBuilder.DefaultPolicyFileName} to opt in (see tutorial/17-governance.md).");
            Console.WriteLine();
            return SlashAction.Continue;
        }

        Console.WriteLine($"Governance: Microsoft.AgentGovernance attached");
        Console.WriteLine($"  agent id : {AgentBuilder.GovernanceAgentId}");
        Console.WriteLine($"  policy   : ./{AgentBuilder.PoliciesDirectoryName}/{AgentBuilder.DefaultPolicyFileName}");

        // Snapshot audit trail under the same lock AgentBuilder uses to
        // write. The list lives behind the kernel's OnAllEvents callback;
        // we read the latest N events.
        AgentGovernance.Audit.GovernanceEvent[] snapshot;
        lock (ctx.AuditTrail)
        {
            snapshot = ctx.AuditTrail.ToArray();
        }

        Console.WriteLine($"  events   : {snapshot.Length} total");
        if (snapshot.Length == 0)
        {
            Console.WriteLine("  (no audit events yet — make a tool call to generate one)");
            Console.WriteLine();
            return SlashAction.Continue;
        }

        // Show the last 5 events. AGT's GovernanceEvent shape is preview;
        // ToString() gives a usable rendering for our purposes.
        Console.WriteLine($"  recent (last {Math.Min(5, snapshot.Length)}):");
        foreach (var evt in snapshot.TakeLast(5))
        {
            Console.WriteLine($"    {evt}");
        }
        Console.WriteLine();
        return SlashAction.Continue;
    }
}

// =============================================================================
//  CompactCommand — on-demand conversation compaction.
//
//  Why a slash command instead of letting the provider auto-run: in MAF
//  preview 1.5.0 the CompactionProvider round-trips the in-flight message
//  list through JSON on every turn, and the polymorphic ToolCall reference
//  inside a live ToolApprovalRequestContent doesn't survive that round-trip.
//  After the user approves a tool, MAF can't bind the response to the
//  original call and the model re-emits the same approval request, looping.
//
//  Workaround: keep ContextWindowCompactionStrategy, drop the provider from
//  the AIContextProviders list (see AgentBuilder.cs), expose compaction as
//  `/compact`. Running between turns means no in-flight ToolCall is exposed
//  to the serialiser — the messages we hand to the reducer are settled.
//
//  Mechanics: build the strategy with the workshop's existing constants,
//  wrap it as IChatReducer via ChatStrategyExtensions.AsChatReducer(), call
//  ReduceAsync on the session's InMemoryChatHistory, write the reduced list
//  back. Sync-over-async because ISlashCommand.Run is sync — same pattern
//  AgentBuilder uses for MCP startup discovery.
// =============================================================================
internal sealed class CompactCommand : ISlashCommand
{
    public string Name => "/compact";
    public string Description => "Reduce conversation history on demand (between turns)";

    public SlashAction Run(SlashContext ctx)
    {
        Console.WriteLine();

        if (!ctx.Session.TryGetInMemoryChatHistory(out var hist) || hist is null || hist.Count == 0)
        {
            Console.WriteLine("  (no chat history to compact yet)");
            Console.WriteLine();
            return SlashAction.Continue;
        }

        var before = hist.Count;
        try
        {
#pragma warning disable MAAI001
            var strategy = new ContextWindowCompactionStrategy(
                maxContextWindowTokens: AgentBuilder.CompactionContextWindowTokens,
                maxOutputTokens:        AgentBuilder.CompactionMaxOutputTokens);

            var reduced = strategy.AsChatReducer()
                .ReduceAsync(hist, CancellationToken.None)
                .GetAwaiter().GetResult()
                .ToList();
#pragma warning restore MAAI001

            ctx.Session.SetInMemoryChatHistory(reduced);

            if (reduced.Count == before)
            {
                Console.WriteLine($"  no compaction needed ({before} messages, still under window)");
            }
            else
            {
                Console.WriteLine($"  compacted: {before} → {reduced.Count} messages");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  compaction failed: {ex.GetType().Name}: {ex.Message}");
        }
        Console.WriteLine();
        return SlashAction.Continue;
    }
}
