// =============================================================================
//  TodoRegistrationTests — pin the recipe AgentBuilder uses to construct
//  the TodoProvider so a refactor can't silently change semantics.
//
//  We don't go through AgentBuilder.Build() because it constructs a real
//  AnthropicClient (needs an API key). Instead we mirror the construction
//  block: new TodoProvider(new TodoProviderOptions { Instructions = ... }).
//  The assertions:
//    - the construction recipe (options-only) doesn't throw;
//    - the produced provider fits the AIContextProvider slot;
//    - the provider's StateKeys includes a TodoProvider-shaped key so
//      session resume picks up the same todo list.
//
//  Real-world coverage for "the model adds, completes, and the list
//  survives across sessions" comes from the live smoke tests.
// =============================================================================

using Microsoft.Agents.AI;

namespace ClaudeChat.Tests;

[Trait("Category", "Unit")]
public sealed class TodoRegistrationTests
{
    [Fact]
    public void Provider_built_with_our_recipe_is_an_AIContextProvider()
    {
        // Mirror AgentBuilder's exact construction recipe.
#pragma warning disable MAAI001
        var provider = new TodoProvider(new TodoProviderOptions
        {
            Instructions = "test instructions",
        });
#pragma warning restore MAAI001

        Assert.NotNull(provider);
        // Must fit through the AIContextProviders slot.
        AIContextProvider asBase = provider;
        Assert.NotNull(asBase);
    }

    [Fact]
    public void Provider_reports_a_TodoProvider_state_key()
    {
        // The framework persists provider state in the session bag. Pin
        // the key here so a future MAF rename gets caught — same defense
        // as FileMemoryRegistrationTests pins "FileMemoryProvider".
#pragma warning disable MAAI001
        var provider = new TodoProvider(new TodoProviderOptions
        {
            Instructions = "test",
        });
#pragma warning restore MAAI001

        Assert.NotEmpty(provider.StateKeys);
        // Be slightly tolerant of capitalization / suffixing while pinning
        // that *some* state key referencing TodoProvider exists. The exact
        // string was empirically observed during the Step 13 live smoke;
        // the assertion below documents what we expect.
        Assert.Contains(provider.StateKeys, key =>
            key.Contains("Todo", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Defaults_do_not_suppress_the_todo_list_message()
    {
        // The whole point of TodoProvider is that the framework injects
        // the current list into the system prompt each turn. If a future
        // MAF change made SuppressTodoListMessage default to true, the
        // workshop would silently lose that injection. Pin the default.
#pragma warning disable MAAI001
        var defaults = new TodoProviderOptions();
#pragma warning restore MAAI001

        Assert.False(defaults.SuppressTodoListMessage);
    }
}
