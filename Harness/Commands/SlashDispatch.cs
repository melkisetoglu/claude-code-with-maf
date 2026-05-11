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
//    - Step 12 (memory) wires /yolo and AlwaysApprove to disk.
// =============================================================================

using Microsoft.Agents.AI;
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
        Console.WriteLine(ctx.Approval.YoloMode
            ? "(yolo: ON — every approval-required tool will auto-approve. /yolo to toggle off.)\n"
            : "(yolo: OFF — approval prompts will return.)\n");
        return SlashAction.Continue;
    }
}
