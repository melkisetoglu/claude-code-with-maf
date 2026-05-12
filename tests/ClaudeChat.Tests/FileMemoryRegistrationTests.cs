// =============================================================================
//  FileMemoryRegistrationTests — pin the recipe AgentBuilder uses to construct
//  the FileMemoryProvider so a refactor can't silently change semantics.
//
//  We don't go through AgentBuilder.Build() because it constructs a real
//  AnthropicClient (needs an API key). Instead we mirror the construction
//  block: FileSystemAgentFileStore(memoryDir) → FileMemoryProvider(store,
//  stateAccessor, options). The assertions:
//    - the constant we wired against matches "memory";
//    - the construction recipe (store + stateAccessor + options) doesn't
//      throw;
//    - the produced provider fits the AIContextProvider slot in
//      ChatClientAgentOptions.AIContextProviders;
//    - the provider's StateKeys includes the framework's persistence key,
//      so resume picks up the same memory state.
//
//  Real-world coverage for "memory survives across sessions" comes from
//  the live smoke tests, where we verified a brand-new session reads back
//  a note the previous session wrote.
// =============================================================================

using ClaudeChat.Agent;
using Microsoft.Agents.AI;

namespace ClaudeChat.Tests;

[Trait("Category", "Unit")]
public sealed class FileMemoryRegistrationTests : IDisposable
{
    private readonly string _tempDir;

    public FileMemoryRegistrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "claudechat-memreg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Constant_pins_the_directory_name()
    {
        // The /memory slash command and AgentBuilder's wiring both reference
        // this. Changing it would split that contract.
        Assert.Equal("memory", AgentBuilder.MemoryDirectoryName);
    }

    [Fact]
    public void Provider_built_with_our_recipe_is_an_AIContextProvider()
    {
        // Mirror AgentBuilder's exact construction recipe.
#pragma warning disable MAAI001
        var store = new FileSystemAgentFileStore(_tempDir);
        var provider = new FileMemoryProvider(
            store,
            _ => new FileMemoryState { WorkingFolder = "" },
            new FileMemoryProviderOptions { Instructions = "test instructions" });
#pragma warning restore MAAI001

        Assert.NotNull(provider);
        // The whole point: must fit through the AIContextProviders slot.
        AIContextProvider asBase = provider;
        Assert.NotNull(asBase);
    }

    [Fact]
    public void Provider_reports_FileMemoryProvider_state_key()
    {
        // The framework persists provider state in the session bag under
        // a "FileMemoryProvider" key (verified live by inspecting saved
        // session JSON). Pin that here so a future MAF rename gets caught.
#pragma warning disable MAAI001
        var store = new FileSystemAgentFileStore(_tempDir);
        var provider = new FileMemoryProvider(
            store,
            _ => new FileMemoryState { WorkingFolder = "" },
            new FileMemoryProviderOptions { Instructions = "test" });
#pragma warning restore MAAI001

        Assert.Contains("FileMemoryProvider", provider.StateKeys);
    }
}
