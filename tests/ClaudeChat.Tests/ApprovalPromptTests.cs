// =============================================================================
//  ApprovalPromptTests — covers the y/N decision logic of the gate.
//
//  Console.In and Console.Out are process-global, so these tests run in a
//  serial collection (no parallelism with other Console-sensitive tests)
//  and restore the streams in their teardown.
// =============================================================================

using ClaudeChat.Harness;
using Microsoft.Extensions.AI;

namespace ClaudeChat.Tests;

[Trait("Category", "Unit")]
[Collection("Console-shared-static")]
public sealed class ApprovalPromptTests : IDisposable
{
    private readonly TextReader _previousIn;
    private readonly TextWriter _previousOut;
    private readonly StringWriter _capturedOut = new();

    public ApprovalPromptTests()
    {
        _previousIn = Console.In;
        _previousOut = Console.Out;
        Console.SetOut(_capturedOut);
    }

    public void Dispose()
    {
        Console.SetIn(_previousIn);
        Console.SetOut(_previousOut);
    }

    [Theory]
    [InlineData("y")]
    [InlineData("Y")]
    [InlineData("yes")]
    [InlineData("YES")]
    [InlineData("  yes  ")]
    public void Approves_on_y_or_yes(string answer)
    {
        Console.SetIn(new StringReader(answer + "\n"));

        var result = ApprovalPrompt.Ask(MakeRequest());

        Assert.True(result);
        Assert.Contains("approved", _capturedOut.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("n")]
    [InlineData("no")]
    [InlineData("nope")]
    [InlineData("maybe")]
    [InlineData(" ")]
    public void Denies_on_anything_else(string answer)
    {
        Console.SetIn(new StringReader(answer + "\n"));

        var result = ApprovalPrompt.Ask(MakeRequest());

        Assert.False(result);
        Assert.Contains("denied", _capturedOut.ToString());
    }

    [Fact]
    public void Denies_on_eof()
    {
        // StringReader with empty content returns null on first ReadLine.
        Console.SetIn(new StringReader(""));

        var result = ApprovalPrompt.Ask(MakeRequest());

        Assert.False(result);
    }

    [Fact]
    public void Renders_tool_name_and_args_in_prompt()
    {
        Console.SetIn(new StringReader("n\n"));

        ApprovalPrompt.Ask(MakeRequest("write_file", ("path", "README.md"), ("contents", "x")));

        var output = _capturedOut.ToString();
        Assert.Contains("write_file", output);
        Assert.Contains("path=\"README.md\"", output);
        Assert.Contains("contents=\"x\"", output);
    }

    private static ToolApprovalRequestContent MakeRequest(
        string toolName = "simulate_action",
        params (string Key, object? Value)[] args)
    {
        var argDict = args.ToDictionary<(string Key, object? Value), string, object?>(
            kv => kv.Key,
            kv => kv.Value);
        var call = new FunctionCallContent("call-1", toolName, argDict);
        return new ToolApprovalRequestContent("req-1", call);
    }
}
