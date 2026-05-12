// =============================================================================
//  SlashRegistryTests — dispatch behaviour: known/unknown commands, case-
//  insensitive, slash arg ignored, non-slash returns null. Also covers
//  each command's user-visible effect by inspecting captured Console.Out.
//
//  Console.Out is process-global → tests share a serial collection.
// =============================================================================

using ClaudeChat.Agent;
using ClaudeChat.Config;
using ClaudeChat.Harness.Commands;
using ClaudeChat.Observability;

namespace ClaudeChat.Tests;

[Trait("Category", "Unit")]
[Collection("Console-shared-static")]
public sealed class SlashRegistryTests : IDisposable
{
    private readonly TextWriter _previousOut;
    private readonly StringWriter _capturedOut = new();

    public SlashRegistryTests()
    {
        _previousOut = Console.Out;
        Console.SetOut(_capturedOut);
    }

    public void Dispose()
    {
        Console.SetOut(_previousOut);
    }

    private static SlashContext MakeContext(
        ApprovalState? approval = null,
        UsageAccumulator? sessionUsage = null,
        AgentConfig? config = null) =>
        new()
        {
            SessionId = "abc12345",
            Session = null!,
            CreatedAt = DateTime.UtcNow,
            Preview = null,
            Agent = null!,
            Model = "claude-haiku-4-5",
            Config = config,
            SessionUsage = sessionUsage ?? new UsageAccumulator(),
            Approval = approval ?? new ApprovalState(),
        };

    [Fact]
    public void Returns_null_for_non_slash_input()
    {
        var registry = SlashRegistry.Default();
        Assert.Null(registry.TryDispatch("hello world", MakeContext()));
        Assert.Null(registry.TryDispatch("", MakeContext()));
    }

    [Fact]
    public void Exit_returns_Exit_action()
    {
        var registry = SlashRegistry.Default();

        Assert.Equal(SlashAction.Exit, registry.TryDispatch("/exit", MakeContext()));
        Assert.Equal(SlashAction.Exit, registry.TryDispatch("/quit", MakeContext()));
    }

    [Fact]
    public void Unknown_command_returns_Continue_with_hint()
    {
        var registry = SlashRegistry.Default();

        var result = registry.TryDispatch("/nonexistent", MakeContext());

        Assert.Equal(SlashAction.Continue, result);
        var output = _capturedOut.ToString();
        Assert.Contains("unknown command", output);
        Assert.Contains("/help", output);
    }

    [Fact]
    public void Dispatch_is_case_insensitive()
    {
        var registry = SlashRegistry.Default();
        Assert.Equal(SlashAction.Exit, registry.TryDispatch("/EXIT", MakeContext()));
        Assert.Equal(SlashAction.Exit, registry.TryDispatch("/Quit", MakeContext()));
    }

    [Fact]
    public void Dispatch_ignores_trailing_args()
    {
        var registry = SlashRegistry.Default();
        Assert.Equal(SlashAction.Exit, registry.TryDispatch("/exit something", MakeContext()));
    }

    [Fact]
    public void Help_lists_all_registered_commands()
    {
        var registry = SlashRegistry.Default();

        var result = registry.TryDispatch("/help", MakeContext());

        Assert.Equal(SlashAction.Continue, result);
        var output = _capturedOut.ToString();
        // Each command shows up by name.
        Assert.Contains("/exit", output);
        Assert.Contains("/help", output);
        Assert.Contains("/clear", output);
        Assert.Contains("/id", output);
        Assert.Contains("/cost", output);
        Assert.Contains("/model", output);
        Assert.Contains("/tools", output);
        Assert.Contains("/sessions", output);
        Assert.Contains("/yolo", output);
        Assert.Contains("/plan", output);
        Assert.Contains("/skills", output);
    }

    [Fact]
    public void Id_command_prints_session_id()
    {
        var registry = SlashRegistry.Default();
        var ctx = MakeContext();
        ctx.SessionId = "deadbeef";

        registry.TryDispatch("/id", ctx);

        Assert.Contains("deadbeef", _capturedOut.ToString());
    }

    [Fact]
    public void Model_command_prints_model_name()
    {
        var registry = SlashRegistry.Default();

        registry.TryDispatch("/model", MakeContext());

        Assert.Contains("claude-haiku-4-5", _capturedOut.ToString());
    }

    [Fact]
    public void Cost_command_prints_session_label_and_zeroes_initially()
    {
        var registry = SlashRegistry.Default();

        registry.TryDispatch("/cost", MakeContext());

        var output = _capturedOut.ToString();
        Assert.Contains("session:", output);
        Assert.Contains("0 in", output);
        Assert.Contains("0 out", output);
    }

    [Fact]
    public void Yolo_command_toggles_state_and_prints_status()
    {
        var registry = SlashRegistry.Default();
        var approval = new ApprovalState();
        var ctx = MakeContext(approval: approval);

        registry.TryDispatch("/yolo", ctx);
        Assert.True(approval.YoloMode);
        Assert.Contains("yolo: ON", _capturedOut.ToString());

        registry.TryDispatch("/yolo", ctx);
        Assert.False(approval.YoloMode);
        Assert.Contains("yolo: OFF", _capturedOut.ToString());
    }

    // ---------- Step 8: /plan ----------

    [Fact]
    public void Plan_command_toggles_state_and_prints_status()
    {
        var registry = SlashRegistry.Default();
        var approval = new ApprovalState();
        var ctx = MakeContext(approval: approval);

        registry.TryDispatch("/plan", ctx);
        Assert.True(approval.PlanMode);
        Assert.Contains("plan: ON", _capturedOut.ToString());

        registry.TryDispatch("/plan", ctx);
        Assert.False(approval.PlanMode);
        Assert.Contains("plan: OFF", _capturedOut.ToString());
    }

    [Fact]
    public void Plan_command_disables_yolo_with_warning()
    {
        var registry = SlashRegistry.Default();
        var approval = new ApprovalState { YoloMode = true };
        var ctx = MakeContext(approval: approval);

        registry.TryDispatch("/plan", ctx);

        Assert.True(approval.PlanMode);
        Assert.False(approval.YoloMode);
        Assert.Contains("turning it off", _capturedOut.ToString());
    }

    [Fact]
    public void Yolo_command_disables_plan_with_warning()
    {
        var registry = SlashRegistry.Default();
        var approval = new ApprovalState { PlanMode = true };
        var ctx = MakeContext(approval: approval);

        registry.TryDispatch("/yolo", ctx);

        Assert.True(approval.YoloMode);
        Assert.False(approval.PlanMode);
        Assert.Contains("turning it off", _capturedOut.ToString());
    }

    [Fact]
    public void Tools_command_lists_default_tools_with_approval_markers()
    {
        var registry = SlashRegistry.Default();

        registry.TryDispatch("/tools", MakeContext());

        var output = _capturedOut.ToString();
        Assert.Contains("read_file", output);
        Assert.Contains("bash", output);
        // bash is approval-required by default — should have [approval] marker.
        Assert.Contains("bash", output);
        Assert.Contains("[approval]", output);
    }

    [Fact]
    public void Tools_command_respects_config_allow_list()
    {
        var registry = SlashRegistry.Default();
        var config = new AgentConfig(
            Model: null,
            Instructions: null,
            Tools: new ToolsConfig(Allow: new[] { "read_file", "grep" }, RequireApproval: null));

        registry.TryDispatch("/tools", MakeContext(config: config));

        var output = _capturedOut.ToString();
        Assert.Contains("read_file", output);
        Assert.Contains("grep", output);
        Assert.DoesNotContain("bash", output);
        Assert.DoesNotContain("write_file", output);
    }
}
