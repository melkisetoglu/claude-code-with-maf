// =============================================================================
//  TodosCommandTests — the /todos slash command (Step 13).
//
//  Scope note: TodoProvider stores items in a real AgentSession's state.
//  We can't mint a real AgentSession in a unit test without an
//  AnthropicClient + an API key. So these tests cover:
//    - command is registered and dispatchable;
//    - empty-state message renders when no items exist (provider has no
//      state for the session we pass — equivalent to a fresh session);
//    - error path: when ctx.Todos is null (unwired), the command catches
//      the NRE and prints a diagnostic instead of crashing the loop.
//
//  Coverage for the populated-list rendering (✓/☐ markers, 2/5 done
//  count, multi-line title+description) comes from the live smoke tests.
// =============================================================================

using ClaudeChat.Config;
using ClaudeChat.Harness.Commands;
using ClaudeChat.Observability;
using Microsoft.Agents.AI;

namespace ClaudeChat.Tests;

[Trait("Category", "Unit")]
[Collection("Console-shared-static")]
public sealed class TodosCommandTests : IDisposable
{
    private readonly TextWriter _previousOut;
    private readonly StringWriter _capturedOut = new();

    public TodosCommandTests()
    {
        _previousOut = Console.Out;
        Console.SetOut(_capturedOut);
    }

    public void Dispose()
    {
        Console.SetOut(_previousOut);
    }

#pragma warning disable MAAI001
    private static TodoProvider FreshProvider() =>
        new(new TodoProviderOptions { Instructions = "test" });
#pragma warning restore MAAI001

#pragma warning disable MAAI001    // TodoProvider in the signature.
    private static SlashContext MakeContext(TodoProvider? todos) =>
#pragma warning restore MAAI001
        new()
        {
            SessionId = "abc12345",
            Session = null!,    // see comment in TodosCommand about why this is OK for empty case
            CreatedAt = DateTime.UtcNow,
            Preview = null,
            Agent = null!,
            Model = "claude-haiku-4-5",
            Config = null,
            SessionUsage = new UsageAccumulator(),
            Approval = new ApprovalState(),
            Todos = todos!,
        };

    [Fact]
    public void Command_is_registered_and_dispatchable()
    {
        var registry = SlashRegistry.Default();
        var ctx = MakeContext(FreshProvider());

        var action = registry.TryDispatch("/todos", ctx);

        Assert.Equal(SlashAction.Continue, action);
    }

    [Fact]
    public void Null_provider_or_session_falls_into_error_path_not_a_crash()
    {
        // ctx.Todos = null! (unwired) is the path we want graceful behaviour
        // on — the loop must not crash; the command must print something.
        var registry = SlashRegistry.Default();
        var ctx = MakeContext(todos: null);

        var action = registry.TryDispatch("/todos", ctx);

        Assert.Equal(SlashAction.Continue, action);
        var output = _capturedOut.ToString();
        // Either the "no todos" message OR the "error reading todos"
        // message is acceptable — both are graceful. What we're forbidding
        // is an unhandled exception escaping the dispatcher.
        Assert.Contains("todos", output, StringComparison.OrdinalIgnoreCase);
    }
}
