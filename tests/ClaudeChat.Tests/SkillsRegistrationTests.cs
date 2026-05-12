// =============================================================================
//  SkillsRegistrationTests — pin the recipe AgentBuilder uses to construct
//  the AgentSkillsProvider so that an unrelated refactor can't silently
//  change discovery semantics.
//
//  We don't go through AgentBuilder.Build() because it constructs a real
//  AnthropicClient (needs an API key). Instead we mirror the construction
//  block: AgentSkillsProviderBuilder → UseFileSkill(dir, opts, null) →
//  Build(). The assertion is the loose contract that matters to us:
//    - the constant we wired against matches "skills";
//    - the builder accepts a directory + empty AgentFileSkillsSourceOptions
//      + null script-runner combination without throwing;
//    - the produced provider can be put in the AIContextProvider[] list
//      that ChatClientAgentOptions.AIContextProviders expects.
//
//  Real-world coverage for "the model actually sees skill content" comes
//  from the smoke tests, where we run the workshop binary and verify the
//  model knows the [step-NN] commit prefix only when skills/repo-context.md
//  is on disk.
// =============================================================================

using ClaudeChat.Agent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeChat.Tests;

[Trait("Category", "Unit")]
public sealed class SkillsRegistrationTests : IDisposable
{
    private readonly string _tempDir;

    public SkillsRegistrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "claudechat-skillsreg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Constants_pin_the_directory_name()
    {
        // The /skills slash command and AgentBuilder's wiring both reference
        // this constant. Changing it would split that contract.
        Assert.Equal("skills", AgentBuilder.SkillsDirectoryName);
    }

    [Fact]
    public void Provider_built_with_our_recipe_is_an_AIContextProvider()
    {
        // Mirror AgentBuilder's exact construction recipe + the framework's
        // discovery layout: each skill is a folder with a SKILL.md manifest.
        var sampleDir = Path.Combine(_tempDir, "sample");
        Directory.CreateDirectory(sampleDir);
        File.WriteAllText(
            Path.Combine(sampleDir, "SKILL.md"),
            "---\nname: sample\ndescription: pinned-by-test\n---\nbody\n");

#pragma warning disable MAAI001
        // Mirror the deny-runner pattern from AgentBuilder.cs — Build() rejects null.
        AgentFileSkillScriptRunner denyRunner =
            (_, _, _, _, _) => throw new InvalidOperationException("test runner");
        var provider = new AgentSkillsProviderBuilder()
            .UseFileSkill(_tempDir, new AgentFileSkillsSourceOptions(), denyRunner)
            .UseLoggerFactory(NullLoggerFactory.Instance)
            .Build();
#pragma warning restore MAAI001

        Assert.NotNull(provider);
        // The whole point: the thing we build must fit through the
        // AIContextProviders slot in ChatClientAgentOptions.
        AIContextProvider asBase = provider;
        Assert.NotNull(asBase);
    }
}
