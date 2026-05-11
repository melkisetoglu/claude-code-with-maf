// =============================================================================
//  AgentBuilder — the one place we assemble the AIAgent.
//
//  Step 0: AnthropicClient + AsAIAgent.
//  Step 1: + read_file tool.
//  Step 2: + list_dir, glob, grep — the read-only navigation toolset.
//  Step 3: + ToolApprovalAgent wrapper. simulate_action demo tool exercised
//          the gate; deleted in Step 4 in favour of real mutators.
//  Step 4: + write_file, edit_file, bash — the mutation tools, every one
//          marked ApprovalRequiredAIFunction. The agent stops being read-only.
//  Step 5: + LoggingAgent (always on) + OpenTelemetryAgent (opt-in via --otel).
//          Observability for the runtime, with token/cost reported per-turn
//          from UsageContent in the stream (see ChatLoop).
//  Future steps wire more in here without touching Program.cs:
//    - Step 10:  CompactionProvider
//    - Step 11+: AgentSkillsProvider, FileMemoryProvider, TodoProvider, …
//
//  Wrap order is inside-out: raw model → LoggingAgent → OpenTelemetryAgent →
//  ToolApprovalAgent. The principle is "outer is closer to the user":
//    - LoggingAgent logs raw model behaviour AS IT HAPPENS — including the
//      approval-response messages the gate forwards back, so the trace is
//      complete.
//    - OpenTelemetryAgent traces the same boundary. (Step 14 may move it.)
//    - ToolApprovalAgent intercepts at the user-visible boundary so the
//      gate is the last thing between the model and the world.
// =============================================================================

using System.Text.Json;
using Anthropic;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ClaudeChat.Tools;

namespace ClaudeChat.Agent;

public static class AgentBuilder
{
    public const string OtelSourceName = "ClaudeChat";

    public static AIAgent Build(
        string apiKey,
        string model,
        ILoggerFactory loggerFactory,
        bool enableOtel = false)
    {
        AnthropicClient client = new() { ApiKey = apiKey };

        // Tools: plain .NET methods wrapped by AIFunctionFactory. The
        // [Description] attributes on the method and its parameters become
        // the JSON schema the model sees — that's how the model knows when
        // and how to call them.
        //
        // Every mutation tool is wrapped in ApprovalRequiredAIFunction. That
        // marker is what the ToolApprovalAgent (below) keys off to emit a
        // ToolApprovalRequestContent in the stream instead of running it.
        // Read-only tools are NOT wrapped — they auto-invoke as before.
        var tools = new List<AITool>
        {
            // Read-only — auto-invoke.
            AIFunctionFactory.Create(ReadFile.Read, name: "read_file"),
            AIFunctionFactory.Create(ListDir.Run,  name: "list_dir"),
            AIFunctionFactory.Create(Glob.Run,     name: "glob"),
            AIFunctionFactory.Create(Grep.Run,     name: "grep"),

            // Mutation — every call routes through the approval gate.
            new ApprovalRequiredAIFunction(
                AIFunctionFactory.Create(WriteFile.Run, name: "write_file")),
            new ApprovalRequiredAIFunction(
                AIFunctionFactory.Create(EditFile.Run,  name: "edit_file")),
            new ApprovalRequiredAIFunction(
                AIFunctionFactory.Create(Bash.Run,      name: "bash")),
        };

        AIAgent inner = client.AsAIAgent(
            model: model,
            name: "ClaudeChat",
            instructions: "You are a helpful assistant. Keep replies concise. " +
                          "Read-only navigation: read_file, list_dir, glob, grep — auto-invoked. " +
                          "Mutation tools: write_file (create/overwrite), edit_file (literal " +
                          "find-and-replace in an existing file), bash (run shell commands) — " +
                          "every call requires explicit user approval. " +
                          "Prefer the smallest tool that does the job: edit_file for targeted " +
                          "changes, write_file for new files or full rewrites, bash only for " +
                          "operations the file tools can't do (running tests, git, build, etc.). " +
                          "Write specific, narrow shell commands — they are easier for the user " +
                          "to read and approve.",
            tools: tools);

        // Step 5: log every Run* call at the model boundary.
        inner = new LoggingAgent(inner, loggerFactory.CreateLogger("ClaudeChat.Agent"));

        // Step 5 (opt-in): tracing. Spans emit to whatever TracerProvider
        // the caller wired up — for the workshop, that's the console
        // exporter set up in Program.cs when --otel is passed.
        if (enableOtel)
        {
            inner = new OpenTelemetryAgent(inner, OtelSourceName);
        }

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
