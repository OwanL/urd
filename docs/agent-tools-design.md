# Agent tools — design decisions

**Status: in owner design review (started 2026-11-10).** Records agreed decisions as they are
made. Class shapes and contracts are still to be proposed; no implementation is authorized yet.
Related: [execution](execution-design.md), [sessions](session-design.md), [discovery](discovery-design.md).

## Agreed

1. **Authority.** Full access, no approval prompts. Tools run as the user on the backend machine.
   A chat's working folder is the default for relative paths, not a boundary. Approval, if ever
   wanted, is a pre-dispatch check added later; it is not designed now.
2. **Starting tool set.** Follow Pi: `bash`, `read`, `write`, `edit`. Copy Pi's behavior where
   it is clear-cut.
3. **One implementation, per-family surfaces.** Models carry model-family metadata. A tool has
   one implementation; core tools (`read`, `write`, `edit`, possibly `bash`) may own an opt-in,
   lightweight surface per family (name, schema, description, argument/result translation) so
   each family gets the shape it was trained on. Bespoke tools have one simple, well-documented
   surface. Context pruning must account for surfaces. Implemented as separate tool classes
   per name; see 13.
4. **Raw calls only.** Store each call as the model emitted it. The stored tool name identifies
   the surface, and that surface interprets its own raw calls for the UI and pruning. No
   canonical copy. Switching model family mid-chat is allowed and is the user's responsibility;
   no conversion layer.
5. **Shell.** `bash` via Git for Windows (as Pi), launched as `bash -c` — never a login shell
   (measured ~690 ms vs ~60 ms per call on the owner's machine). Urd supplies the environment.
   Missing Git Bash is a clear startup/tool error. A persistent shell is deferred. Calls record
   their duration.
6. **Parallel calls.** Calls emitted in one reply run in parallel; results return in call order;
   each call returns its own result — a failure never cancels siblings. Mutations of the same
   file are serialized (Pi's per-file queue); a read of a file waits for pending mutations to it.
   No batch/meta tool: evidence shows models misuse them and harnesses removed them. If order
   matters, the model uses separate replies or chains commands in bash. Script-based tool
   orchestration ("code mode") is possible future work, not this scope.
7. **Background calls.** Any tool call may set `background: true`. The harness provides this
   generically; tool implementations are unaware of it. The call immediately gets a placeholder
   result naming the pending call; the model keeps working. The real result is appended as a
   message at the next opportune point — after the current set of tool results (providers
   require results to follow their calls directly), or by starting a new turn if the agent is
   idle. Background calls are for finite work.
8. **Background-call control.** Two tools: list pending calls, and cancel all pending calls.
9. **Long-lived processes** (dev servers) use plain bash backgrounding (`cmd > log 2>&1 &`),
   read with `read`, stopped with `kill` — as Pi. Required process rules:
   - a bash call completes when the shell exits, not when all output pipes close;
   - processes left behind outlive the call and belong to the chat;
   - they are killed when the chat closes or Urd exits (one Windows job object per chat, so a
     crash also cleans up).
10. **Stop** cancels the current run's work including its pending background calls; it does not
    kill processes deliberately left running by earlier bash calls.

11. **Tool behavior details** (output truncation and spill files, `read` paging and images,
    `write` folder creation, timeouts) copy Pi. Exception: `edit` uses exact matching only,
    treating CRLF/LF as equal; no fuzzy fallback (Pi's normalizes whole lines and can change
    text the model never targeted), and it never rewrites invalid UTF-8 silently.
12. **Crash.** A call interrupted by an Urd crash, background or not, is reported to the model
    as interrupted and possibly partly applied. It is never re-run.
13. **Tool shape (approved code).** One class per model-facing tool name; shared behavior lives
    in ordinary helpers (e.g. `FileEditor` used by both `EditTool` and `ApplyPatchTool`). No
    separate "surface" abstraction. A `ToolCatalog` picks the tools a chat offers per model
    family and finds any tool by name for saved calls. Urd owns `AgentTool` rather than using
    `AIFunction` directly, converting to provider declarations at the edge.

    ```csharp
    internal abstract class AgentTool {
      public abstract string Name { get; }
      public abstract string Description { get; }
      public abstract JsonElement ParametersSchema { get; }
      public abstract Task<ToolOutput> RunAsync(
        JsonElement arguments, ToolContext context, CancellationToken cancellation);
    }

    internal sealed class ToolCatalog {
      public IReadOnlyList<AgentTool> For(ModelFamily family);
      public AgentTool? Find(string name);
    }
    ```
14. **Waiting on background calls.** When the agent's reply has ended but background calls are
    pending, the chat shows a waiting status and Stop remains available; Stop cancels them.
    Completion wakes the agent and the chat returns to running.
15. **Model family** is Urd-owned metadata on the model's saved settings: OpenAI, Anthropic,
    Google, Other.
16. **Context overflow** is handled by automatic history compaction (summarizing older
    conversation), which is distinct from tool-result pruning (see ARCHITECTURE.md). Compaction
    is the next design topic after tools; tools are not ready for normal use without it.
17. **Background on every tool** was re-examined after review and kept: every tool may run in
    the background, including future tools.

## Open

- Remaining class structure: none; all ten pieces approved.
- Transcript presentation of tool rows and pending background calls.
- Compaction design.

## Approved code shapes

Item 13 holds `AgentTool` and `ToolCatalog`. Further approved pieces, owner-reviewed one at a time:

**Call input and output.**

```csharp
internal sealed record ToolContext(
  string WorkingDirectory, Guid RequestId, string CallId,
  bool ModelAcceptsImages, Action<ToolOutput> ReportProgress);

internal sealed record ToolOutput(
  string Text, IReadOnlyList<DataContent> Images,
  JsonElement? Details = null, bool IsError = false);
```

**Model settings.** Version 1 offers the generic tools to every family; a family without its own
tool falls back to the generic one. Family-specific tools (e.g. `apply_patch`) come later.

```csharp
public enum ModelFamily { OpenAI, Anthropic, Google, Other }
public sealed record ModelSettings(Guid ConnectionId, string ModelId, ModelFamily Family);
public sealed record LlmSelection(
  ProviderConnection Connection, string ModelId, string? ReasoningEffort, ModelFamily Family);
```

**File tools.** Each tool owns its logic; extract shared helpers only when a second user exists.
The queue is shared by all chats and coordinates Urd's own tools only.

```csharp
internal sealed class FileMutationQueue {
  public Task<T> ChangeAsync<T>(string path, Func<CancellationToken, Task<T>> change, CancellationToken cancellation);
  public Task<T> ReadAsync<T>(string path, Func<CancellationToken, Task<T>> read, CancellationToken cancellation);
}
internal sealed class ReadTool(FileMutationQueue files) : AgentTool { ... }   // read(path, offset?, limit?)
internal sealed class WriteTool(FileMutationQueue files) : AgentTool { ... }  // write(path, content)
internal sealed class EditTool(FileMutationQueue files) : AgentTool { ... }   // edit(path, edits)
```

**Parallel dispatch** (existing `SessionExecution`): save the reply's calls, start all, save
each result independently, return results in call order. A tool error is that call's result.
The harness adds and strips the `background` field; only tools offered to this chat's model run.

**Pending background calls.**

```csharp
internal sealed class PendingToolCalls {
  public void Add(Guid executionId, Guid requestId, LlmToolCall call,
    Task completion, CancellationTokenSource cancellation);
  public IReadOnlyList<PendingToolCall> List();
  public int RequestCancelAll(Guid callerRequestId, string callerCallId);
  public void RequestCancelExecution(Guid executionId);
  public Task JoinExecutionAsync(Guid executionId);
}
internal sealed record PendingToolCall(
  Guid RequestId, string CallId, string Name, DateTimeOffset StartedAt, bool CancellationRequested);
internal sealed class ListPendingCallsTool(PendingToolCalls pending) : AgentTool { ... }
internal sealed class CancelPendingCallsTool(PendingToolCalls pending) : AgentTool { ... }
```

**Bash and processes.** One Windows job object per chat; a nested job per call.

```csharp
internal sealed class ChatProcesses : IAsyncDisposable {
  public ShellProcess StartBash(string command, string directory,
    IReadOnlyDictionary<string, string> environment, Action<ReadOnlyMemory<byte>> receiveOutput);
  public Task KillAllAsync();
}
internal sealed class ShellProcess : IAsyncDisposable {
  public Task<int?> WaitForExitAsync();
  public Task CancelAsync();
  public void LeaveBackgroundProcesses();
}
internal sealed class BashTool(ChatProcesses processes, GitBash bash) : AgentTool { ... }  // bash(command, timeout?)
```

**Waiting, completion delivery and Stop** (existing `SessionBase`).

```csharp
internal Task BackgroundToolCompletedAsync(Guid requestId, string callId);
private Task<LlmRequest?> AdvanceExecutionAsync(SessionExecution execution,
  TaskCompletionSource<SessionExecutionResult> settlement, ExecutionOutcome outcome);
internal Task StopExecutionAsync();
private string GetStatus(SessionSnapshot snapshot);  // gains "waiting"
```
- Pending background calls with no next turn: status `waiting`, Stop available. A completion or
  user input wakes the run. Context preparation is the hook for later compaction.
- A background result reaches the model after all direct results as a labelled tool-completion
  envelope (origin, call identity, outcome, result marked as untrusted data), never as a human
  message. The transcript shows it as its own row type.
- Stop cancels the run's provider, foreground and background work and waits for processes and
  saves to finish. Processes left by earlier `&` commands survive. Hiding a tab stops nothing.

**Storage and crash recovery.** No new types.

```csharp
internal bool TryStart(Guid sessionId, Guid executionId, long revision,
  Guid requestId, string callId, bool background);                       // ToolStorage
internal bool Complete(Guid sessionId, Guid executionId, long revision,
  Guid requestId, LlmToolResult result);                                 // ToolStorage
public void RecoverInterruptedExecutions();                              // SessionExecutionStorage
```
- A background call is a saved call with `background = true`; its placeholder is generated from
  that flag, not stored. `Complete` saves a background result and its completion message in one
  transaction; the next-request claim includes waiting completions after direct results.
- Startup marks calls running at crash time "interrupted, possibly partly applied"; nothing re-runs.

**Composition.**

```csharp
public sealed record AgentToolSettings(
  string WorkingDirectory, string? GitBashPath, IReadOnlyDictionary<string, string> Environment);
public SessionManager(UrdStore store, Func<ProviderConnection, IChatClient> createChatClient,
  AgentToolSettings? toolSettings = null);
```
- Core builds the shared file queue and each chat's catalog, pending list, processes and tools.
  No settings means no tools. Each chat saves its working folder at creation (v1: the server's
  configured folder; per-chat choice later).
- Shutdown kills chat processes. Final close does not exist yet; when built it kills the chat's
  processes. Until then `&` processes live until Urd exits.

**Implementation notes from review.** `FileMutationQueue` keys paths by canonical identity
(existing ancestor for new files, Windows case-insensitive, `/c/...` equals `C:\...`). Before
building process handling, qualify real Git Bash and job-object behavior with a minimal fixture:
foreground Stop, survivor after shell exit, sibling isolation, chat/backend termination,
inherited pipes, spaces/Unicode, path forms and encoding.
