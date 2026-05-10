// =============================================================================
//  AgentBuilder — the one place we assemble the AIAgent.
//
//  Step 0: AnthropicClient + AsAIAgent.
//  Step 1: + read_file tool.
//  Step 2: + list_dir, glob, grep — the read-only navigation toolset.
//  Future steps wire more in here without touching Program.cs:
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
            AIFunctionFactory.Create(ListDir.Run,  name: "list_dir"),
            AIFunctionFactory.Create(Glob.Run,     name: "glob"),
            AIFunctionFactory.Create(Grep.Run,     name: "grep"),
        };

        // FunctionInvokingChatClient (under the hood of AsAIAgent when tools
        // are passed) handles the call → invoke → result loop for us. We
        // write the function; MAF/M.E.AI runs it.
        return client.AsAIAgent(
            model: model,
            name: "ClaudeChat",
            instructions: "You are a helpful assistant. Keep replies concise. " +
                          "You can navigate the user's project with these tools: " +
                          "read_file (open a file), list_dir (one directory level), " +
                          "glob (find files by pattern, recursive), grep (search file contents). " +
                          "Prefer glob/grep over guessing paths. " +
                          "Use specific roots/patterns to avoid noisy results.",
            tools: tools);
    }
}
