// =============================================================================
//  ApprovalPromptTests — covers the y/N/a decision logic, yolo bypass, and
//  the AlwaysApprove memory.
//
//  Console.In and Console.Out are process-global, so these tests run in a
//  serial collection (no parallelism with other Console-sensitive tests)
//  and restore the streams in their teardown.
// =============================================================================

using ClaudeChat.Harness;
using ClaudeChat.Harness.Commands;
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

        var result = ApprovalPrompt.Ask(MakeRequest(), new ApprovalState());

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

        var result = ApprovalPrompt.Ask(MakeRequest(), new ApprovalState());

        Assert.False(result);
        Assert.Contains("denied", _capturedOut.ToString());
    }

    [Fact]
    public void Denies_on_eof()
    {
        Console.SetIn(new StringReader(""));

        var result = ApprovalPrompt.Ask(MakeRequest(), new ApprovalState());

        Assert.False(result);
    }

    [Fact]
    public void Renders_tool_name_and_args_in_prompt()
    {
        Console.SetIn(new StringReader("n\n"));

        ApprovalPrompt.Ask(
            MakeRequest("write_file", ("path", "README.md"), ("contents", "x")),
            new ApprovalState());

        var output = _capturedOut.ToString();
        Assert.Contains("write_file", output);
        Assert.Contains("path=\"README.md\"", output);
        Assert.Contains("contents=\"x\"", output);
    }

    // ---------- Step 7: yolo bypass ----------

    [Fact]
    public void Yolo_mode_auto_approves_without_prompting()
    {
        // No stdin set up — if we prompted, we'd get null and deny.
        // So if approval comes back true here, yolo bypassed the prompt.
        Console.SetIn(new StringReader(""));
        var state = new ApprovalState { YoloMode = true };

        var result = ApprovalPrompt.Ask(MakeRequest("bash"), state);

        Assert.True(result);
        Assert.Contains("auto-approved (yolo)", _capturedOut.ToString());
    }

    // ---------- Step 7: AlwaysApprove memory ----------

    [Fact]
    public void Always_answer_remembers_tool_for_future_calls()
    {
        Console.SetIn(new StringReader("a\n"));
        var state = new ApprovalState();

        var firstResult = ApprovalPrompt.Ask(MakeRequest("write_file"), state);

        Assert.True(firstResult);
        Assert.Contains("write_file", state.AlwaysApprove);
        Assert.Contains("remembering", _capturedOut.ToString());
    }

    [Fact]
    public void Always_answer_full_word_works_too()
    {
        Console.SetIn(new StringReader("always\n"));
        var state = new ApprovalState();

        var result = ApprovalPrompt.Ask(MakeRequest("bash"), state);

        Assert.True(result);
        Assert.Contains("bash", state.AlwaysApprove);
    }

    [Fact]
    public void Tool_in_AlwaysApprove_set_auto_approves_subsequent_calls()
    {
        // No stdin — if we prompted, this would deny.
        Console.SetIn(new StringReader(""));
        var state = new ApprovalState();
        state.AlwaysApprove.Add("write_file");

        var result = ApprovalPrompt.Ask(MakeRequest("write_file"), state);

        Assert.True(result);
        Assert.Contains("auto-approved (always)", _capturedOut.ToString());
    }

    [Fact]
    public void AlwaysApprove_does_not_affect_other_tools()
    {
        Console.SetIn(new StringReader("n\n"));
        var state = new ApprovalState();
        state.AlwaysApprove.Add("write_file");

        // Different tool than the one in AlwaysApprove — should still prompt.
        var result = ApprovalPrompt.Ask(MakeRequest("bash"), state);

        Assert.False(result);
        Assert.DoesNotContain("auto-approved", _capturedOut.ToString());
    }

    // ---------- Step 8: PlanMode auto-deny ----------

    [Fact]
    public void Plan_mode_auto_denies_without_prompting()
    {
        // No stdin — if we prompted, we'd get null/EOF and deny anyway.
        // The point is to verify the "denied: in plan mode" message appears
        // and the fast-path is taken without reading input.
        Console.SetIn(new StringReader(""));
        var state = new ApprovalState { PlanMode = true };

        var result = ApprovalPrompt.Ask(MakeRequest("write_file"), state);

        Assert.False(result);
        Assert.Contains("denied: in plan mode", _capturedOut.ToString());
    }

    [Fact]
    public void Plan_mode_beats_yolo()
    {
        // Both flags on (shouldn't happen via UI, but defensive): plan wins.
        Console.SetIn(new StringReader(""));
        var state = new ApprovalState { PlanMode = true, YoloMode = true };

        var result = ApprovalPrompt.Ask(MakeRequest("bash"), state);

        Assert.False(result);
        Assert.Contains("denied: in plan mode", _capturedOut.ToString());
    }

    [Fact]
    public void Plan_mode_beats_AlwaysApprove()
    {
        Console.SetIn(new StringReader(""));
        var state = new ApprovalState { PlanMode = true };
        state.AlwaysApprove.Add("bash");

        var result = ApprovalPrompt.Ask(MakeRequest("bash"), state);

        Assert.False(result);
        Assert.Contains("denied: in plan mode", _capturedOut.ToString());
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
