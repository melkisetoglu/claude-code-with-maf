// =============================================================================
//  AgentBuilder — the one place we assemble the AIAgent.
//
//  Step 0: AnthropicClient + AsAIAgent.
//  Step 1: + read_file tool.
//  Future steps wire more in here without touching Program.cs:
//    - Step 2:   list_dir, glob, grep
//    - Step 3:   ToolApprovalAgent wrapping
//    - Step 4:   write_file, edit_file, bash (gated by Step 3)
//    - Step 5:   LoggingAgent + OpenTelemetry
//    - Step 10:  CompactionProvider
//    - Step 11+: AgentSkillsProvider, FileMemoryProvider, TodoProvider, …
//
//  The point of MAF is that all of those are written against AIAgent, not
//  Anthropic-specific types. This file is where the seam lives.
// =============================================================================

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
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(ReadFile.Read, name: "read_file"),
        };

        // FunctionInvokingChatClient (under the hood of AsAIAgent when tools
        // are passed) handles the call → invoke → result loop for us. We
        // write the function; MAF/M.E.AI runs it.
        return client.AsAIAgent(
            model: model,
            name: "ClaudeChat",
            instructions: "You are a helpful assistant. Keep replies concise. " +
                          "When you need the contents of a file, call the read_file tool.",
            tools: tools);
    }
}
