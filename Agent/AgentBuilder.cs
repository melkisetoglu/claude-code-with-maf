// =============================================================================
//  AgentBuilder — the one place we assemble the AIAgent.
//
//  Step 0: AnthropicClient + AsAIAgent.
//  Step 1: + read_file tool.
//  Step 2: + list_dir, glob, grep — the read-only navigation toolset.
//  Step 3: + ToolApprovalAgent wrapper, + simulate_action (approval-required
//          demo tool that exercises the gate; replaced by real mutators in
//          Step 4).
//  Future steps wire more in here without touching Program.cs:
//    - Step 4:   write_file, edit_file, bash (gated by Step 3)
//    - Step 5:   LoggingAgent + OpenTelemetry
//    - Step 10:  CompactionProvider
//    - Step 11+: AgentSkillsProvider, FileMemoryProvider, TodoProvider, …
//
//  This is also where the **delegating-agent pattern** first appears in the
//  workshop. ToolApprovalAgent inherits from DelegatingAIAgent — it wraps
//  another AIAgent and intercepts the streaming flow, splitting any tool
//  call marked approval-required into a request/response handshake instead
//  of auto-invoking. Steps 5 (LoggingAgent) and 14 (hooks/middleware) layer
//  more delegating agents on top — same pattern, different concern.
// =============================================================================

using System.Text.Json;
using Anthropic;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ClaudeChat.Tools;

namespace ClaudeChat.Agent;

public static class AgentBuilder
{
    public static AIAgent Build(string apiKey, string model)
    {
        AnthropicClient client = new() { ApiKey = apiKey };

        // Tools: plain .NET methods wrapped by AIFunctionFactory. The
        // [Description] attributes on the method and its parameters become
        // the JSON schema the model sees — that's how the model knows when
        // and how to call them.
        //
        // simulate_action is wrapped in ApprovalRequiredAIFunction. That
        // marker is what the ToolApprovalAgent (below) keys off to emit a
        // ToolApprovalRequestContent in the stream instead of running it.
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(ReadFile.Read, name: "read_file"),
            AIFunctionFactory.Create(ListDir.Run,  name: "list_dir"),
            AIFunctionFactory.Create(Glob.Run,     name: "glob"),
            AIFunctionFactory.Create(Grep.Run,     name: "grep"),
            new ApprovalRequiredAIFunction(
                AIFunctionFactory.Create(SimulateAction.Run, name: "simulate_action")),
        };

        AIAgent inner = client.AsAIAgent(
            model: model,
            name: "ClaudeChat",
            instructions: "You are a helpful assistant. Keep replies concise. " +
                          "Read-only project navigation: read_file, list_dir, glob, grep. " +
                          "Prefer glob/grep over guessing paths; use specific roots/patterns. " +
                          "There is also a simulate_action tool that requires explicit user " +
                          "approval and exists for demoing the approval gate — call it when " +
                          "the user asks to 'simulate' or to demo a dangerous action.",
            tools: tools);

        // Wrap once with the approval gate. Anything marked
        // ApprovalRequiredAIFunction will route through here.
        // JsonSerializerOptions are used to (de)serialize tool arguments
        // when the approval round-trip puts them on the wire.
        //
        // ToolApprovalAgent is marked [Experimental] (MAAI001) — the API may
        // change in future MAF previews. We suppress the diagnostic here
        // rather than project-wide because it should stay visible: when MAF
        // moves the type, we want the warning back to flag the migration.
#pragma warning disable MAAI001
        return new ToolApprovalAgent(inner, JsonSerializerOptions.Default);
#pragma warning restore MAAI001
    }
}
