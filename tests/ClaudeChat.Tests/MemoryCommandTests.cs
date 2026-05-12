// =============================================================================
//  MemoryCommandTests — the /memory slash command (Step 12).
//
//  Mirrors SkillsCommandTests: redirect cwd to a temp dir, lay down the
//  expected ./memory/ contents, exercise the command, snapshot Console.Out.
//
//  Joins the Console-shared-static collection because Directory.SetCurrentDirectory
//  is process-global and would race AgentConfigTests / SkillsCommandTests
//  otherwise.
// =============================================================================

using ClaudeChat.Agent;
using ClaudeChat.Config;
using ClaudeChat.Harness.Commands;
using ClaudeChat.Observability;

namespace ClaudeChat.Tests;

[Trait("Category", "Unit")]
[Collection("Console-shared-static")]
public sealed class MemoryCommandTests : IDisposable
{
    private readonly TextWriter _previousOut;
    private readonly StringWriter _capturedOut = new();
    private readonly string _previousCwd;
    private readonly string _tempDir;

    public MemoryCommandTests()
    {
        _previousOut = Console.Out;
        Console.SetOut(_capturedOut);
        _previousCwd = Directory.GetCurrentDirectory();
        _tempDir = Path.Combine(Path.GetTempPath(), "claudechat-memory-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        Directory.SetCurrentDirectory(_tempDir);
    }

    public void Dispose()
    {
        Console.SetOut(_previousOut);
        Directory.SetCurrentDirectory(_previousCwd);
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static SlashContext MakeContext() =>
        new()
        {
            SessionId = "abc12345",
            Session = null!,
            CreatedAt = DateTime.UtcNow,
            Preview = null,
            Agent = null!,
            Model = "claude-haiku-4-5",
            Config = null,
            SessionUsage = new UsageAccumulator(),
            Approval = new ApprovalState(),
        };

    [Fact]
    public void No_memory_dir_prints_opt_in_hint()
    {
        var registry = SlashRegistry.Default();

        var action = registry.TryDispatch("/memory", MakeContext());

        Assert.Equal(SlashAction.Continue, action);
        var output = _capturedOut.ToString();
        Assert.Contains("no ./" + AgentBuilder.MemoryDirectoryName + "/ folder", output);
        Assert.Contains("opt in", output);
    }

    [Fact]
    public void Empty_memory_dir_says_so()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, AgentBuilder.MemoryDirectoryName));
        var registry = SlashRegistry.Default();

        registry.TryDispatch("/memory", MakeContext());

        var output = _capturedOut.ToString();
        Assert.Contains("is empty", output);
    }

    [Fact]
    public void Lists_all_files_in_memory_with_byte_sizes()
    {
        var memDir = Path.Combine(_tempDir, AgentBuilder.MemoryDirectoryName);
        Directory.CreateDirectory(memDir);
        File.WriteAllText(Path.Combine(memDir, "user_preferences.md"),
            "# Preferences\n- terse\n");
        File.WriteAllText(Path.Combine(memDir, "user_preferences_description.md"),
            "Sidecar describing the note.");
        // memories.md is the framework's auto-maintained index.
        File.WriteAllText(Path.Combine(memDir, "memories.md"),
            "# Memory Index\n- user_preferences.md: User communication preferences\n");

        var registry = SlashRegistry.Default();
        registry.TryDispatch("/memory", MakeContext());

        var output = _capturedOut.ToString();
        // Show all three — the framework's bookkeeping files are part of
        // the memory state, hiding them would be misleading.
        Assert.Contains("user_preferences.md", output);
        Assert.Contains("user_preferences_description.md", output);
        Assert.Contains("memories.md", output);
        Assert.Matches(@"user_preferences\.md\s+\(\d+ bytes\)", output);
    }

    [Fact]
    public void Constant_matches_directory_name()
    {
        // Pin the convention — if anyone "improves" this to ".memory" or
        // "Memory" the slash command and the AgentBuilder wiring would
        // diverge silently.
        Assert.Equal("memory", AgentBuilder.MemoryDirectoryName);
    }
}
