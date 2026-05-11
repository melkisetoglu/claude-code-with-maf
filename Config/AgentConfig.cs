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
    ToolsConfig? Tools);

public sealed record ToolsConfig(
    IReadOnlyList<string>? Allow,
    IReadOnlyList<string>? RequireApproval);

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
