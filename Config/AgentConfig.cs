// =============================================================================
//  AgentConfig — the on-disk shape of agent.json + the in-memory record that
//  AgentBuilder reads.
//
//  Every field is optional. Missing/null fields fall back to the same defaults
//  the workshop has been using all along, so a project without agent.json
//  behaves exactly as Step 5 did.
//
//  Discovery order in Program.cs:
//    1. --config <path>         (explicit, fails loudly if path missing)
//    2. ./agent.json            (auto-discovered, silent if missing)
//    3. built-in defaults
//
//  Naming: camelCase in JSON (`{ "requireApproval": [...] }`), PascalCase
//  in C# (`RequireApproval`). System.Text.Json bridges via the camelCase
//  naming policy.
// =============================================================================

using System.Text.Json;

namespace ClaudeChat.Config;

public sealed record AgentConfig(
    string? Model,
    string? Instructions,
    ToolsConfig? Tools,
    IReadOnlyList<McpServerConfig>? McpServers);

public sealed record ToolsConfig(
    IReadOnlyList<string>? Allow,
    IReadOnlyList<string>? RequireApproval);

/// <summary>
/// Step 16 — describes one MCP server the agent should expose to the model.
///
/// Each entry becomes a direct <c>McpClientTool</c> via
/// <c>ModelContextProtocol.Client.McpClient</c> over <c>HttpClientTransport</c>.
/// We bypass <c>Microsoft.Extensions.AI.HostedMcpServerTool</c> intentionally
/// — see <c>AgentBuilder.AppendMcpServerToolsAsync</c> and its comment for why
/// that path is broken for approval-required MCP tools in this MAF preview.
///
/// Fields:
///   - <see cref="Name"/>     : friendly identifier shown to the model + in /tools.
///   - <see cref="Address"/>  : full URL, e.g. "http://localhost:8931/mcp" for
///                              a locally-running Playwright MCP server started with
///                              <c>npx @playwright/mcp@latest --port 8931</c>.
///   - <see cref="Description"/>: optional human-readable description that flows
///                              into the model's system prompt.
///   - <see cref="ApprovalMode"/>: "always" (default) / "never" — gating policy.
///                              Object form for per-tool granularity is a stretch.
///   - <see cref="AllowedTools"/>: optional allowlist of server-side tool names
///                              the model is permitted to call.
///   - <see cref="Headers"/>  : optional HTTP headers (e.g. for bearer auth).
/// </summary>
public sealed record McpServerConfig(
    string Name,
    string Address,
    string? Description,
    string? ApprovalMode,
    IReadOnlyList<string>? AllowedTools,
    IReadOnlyDictionary<string, string>? Headers);

public static class AgentConfigLoader
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Load config from an explicit path. Throws if the path doesn't exist or
    /// the file isn't valid JSON / the schema is wrong.
    /// </summary>
    public static AgentConfig LoadFromPath(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"agent config not found: {path}");

        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<AgentConfig>(json, JsonOpts)
            ?? throw new InvalidDataException($"agent config at {path} parsed as null");
        return config;
    }

    /// <summary>
    /// Try to load ./agent.json from the current working directory. Returns
    /// null if the file doesn't exist; throws on parse errors so a malformed
    /// file fails loudly instead of silently using defaults.
    /// </summary>
    public static AgentConfig? TryLoadFromCwd()
    {
        const string DefaultName = "agent.json";
        if (!File.Exists(DefaultName)) return null;
        return LoadFromPath(DefaultName);
    }
}
