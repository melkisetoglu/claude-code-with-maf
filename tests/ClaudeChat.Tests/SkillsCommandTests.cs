// =============================================================================
//  SkillsCommandTests — the /skills slash command (Step 11).
//
//  Skills layout convention (matches AgentFileSkillsSource discovery):
//      ./skills/<skill-name>/SKILL.md
//  A flat ./skills/foo.md is silently skipped by the framework; the slash
//  command mirrors that behaviour so the diagnostic matches what the model
//  actually sees.
//
//  /skills scans `${cwd}/skills/` at invocation time (not at AgentBuilder
//  construction), so the test approach is: redirect cwd to a temp dir,
//  populate the expected folder shape, run the command, snapshot Console.Out.
//
//  Sharing Console.Out + cwd both make this serial — same
//  Console-shared-static collection as SlashRegistryTests / AgentConfigTests
//  so the three never interleave.
// =============================================================================

using ClaudeChat.Agent;
using ClaudeChat.Config;
using ClaudeChat.Harness.Commands;
using ClaudeChat.Observability;

namespace ClaudeChat.Tests;

[Trait("Category", "Unit")]
[Collection("Console-shared-static")]
public sealed class SkillsCommandTests : IDisposable
{
    private readonly TextWriter _previousOut;
    private readonly StringWriter _capturedOut = new();
    private readonly string _previousCwd;
    private readonly string _tempDir;

    public SkillsCommandTests()
    {
        _previousOut = Console.Out;
        Console.SetOut(_capturedOut);
        _previousCwd = Directory.GetCurrentDirectory();
        _tempDir = Path.Combine(Path.GetTempPath(), "claudechat-skills-test-" + Guid.NewGuid().ToString("N"));
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
    public void No_skills_dir_prints_opt_in_hint()
    {
        // cwd is the temp dir; no ./skills/ subdir.
        var registry = SlashRegistry.Default();

        var action = registry.TryDispatch("/skills", MakeContext());

        Assert.Equal(SlashAction.Continue, action);
        var output = _capturedOut.ToString();
        Assert.Contains("no ./" + AgentBuilder.SkillsDirectoryName + "/ folder", output);
        Assert.Contains("opt in", output);
    }

    [Fact]
    public void Empty_skills_dir_says_so()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, AgentBuilder.SkillsDirectoryName));
        var registry = SlashRegistry.Default();

        registry.TryDispatch("/skills", MakeContext());

        var output = _capturedOut.ToString();
        Assert.Contains("no <name>/SKILL.md skills", output);
    }

    [Fact]
    public void Lists_skills_by_folder_name_with_byte_sizes()
    {
        var skillsDir = Path.Combine(_tempDir, AgentBuilder.SkillsDirectoryName);
        // Two well-formed skills (each in its own <name>/SKILL.md folder).
        Directory.CreateDirectory(Path.Combine(skillsDir, "alpha"));
        File.WriteAllText(Path.Combine(skillsDir, "alpha", "SKILL.md"),
            "---\nname: alpha\ndescription: a\n---\nbody-a\n");
        Directory.CreateDirectory(Path.Combine(skillsDir, "beta"));
        File.WriteAllText(Path.Combine(skillsDir, "beta", "SKILL.md"),
            "---\nname: beta\ndescription: b\n---\nbody-b\n");

        // A folder without SKILL.md should NOT be listed (framework would skip it too).
        Directory.CreateDirectory(Path.Combine(skillsDir, "incomplete"));
        File.WriteAllText(Path.Combine(skillsDir, "incomplete", "notes.md"), "not a skill manifest");

        // A flat .md at the dir root should also NOT be listed — matches framework behaviour.
        File.WriteAllText(Path.Combine(skillsDir, "loose.md"), "ignored");

        var registry = SlashRegistry.Default();
        registry.TryDispatch("/skills", MakeContext());

        var output = _capturedOut.ToString();
        Assert.Contains("alpha", output);
        Assert.Contains("beta",  output);
        Assert.DoesNotContain("incomplete", output);
        Assert.DoesNotContain("loose", output);
        Assert.Matches(@"alpha\s+\(\d+ bytes\)", output);
    }

    [Fact]
    public void Constant_matches_directory_name()
    {
        // Pin the convention — if anyone "improves" this to ".skills" or
        // "Skills" the slash command and the AgentBuilder wiring would
        // diverge silently.
        Assert.Equal("skills", AgentBuilder.SkillsDirectoryName);
    }
}
