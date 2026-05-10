// =============================================================================
//  AgentBuilder — the one place we assemble the AIAgent.
//
//  Today (Step 0) this is just AnthropicClient + AsAIAgent. Future steps wire
//  more in here without touching Program.cs:
//    - Step 1–4: tools (read_file, list_dir, glob, grep, …)
//    - Step 3:   ToolApprovalAgent wrapping
//    - Step 5:   LoggingAgent + OpenTelemetry
//    - Step 10:  CompactionProvider
//    - Step 11+: AgentSkillsProvider, FileMemoryProvider, TodoProvider, …
//
//  The point of MAF is that all of those are written against AIAgent, not
//  Anthropic-specific types. This file is where the seam lives.
// =============================================================================

using Anthropic;
using Microsoft.Agents.AI;

namespace ClaudeChat.Agent;

public static class AgentBuilder
{
    public static AIAgent Build(string apiKey, string model)
    {
        AnthropicClient client = new() { ApiKey = apiKey };

        return client.AsAIAgent(
            model: model,
            name: "ClaudeChat",
            instructions: "You are a helpful assistant. Keep replies concise.");
    }
}
