# Agent tools — design decisions

**Status: design approved by the owner 2026-11-10.** Records agreed decisions as they were
made, and the approved class shapes. Implementation follows these shapes, subject to the
qualification gates below; approval is not evidence that the integration already works.
Related: [execution](execution-design.md), [sessions](session-design.md), [discovery](discovery-design.md).
[Qualification evidence](agent-tools-qualification.md) records the bounded process and adapter
probes and their remaining integration limits. The 2026-11-11 checkpoints qualify generic
parallel tools, explicit model metadata, persisted images, background delivery/waiting, Stop
and explicit startup recovery. Compaction and rich tool-result inspection remain pending.

## Agreed

1. **Authority.** Full access, no approval prompts. Tools run as the user on the backend machine.
   A chat's working folder is the default for relative paths, not a boundary. Approval, if ever
   wanted, is a pre-dispatch check added later; it is not designed now.
2. **Starting tool set.** Follow Pi: `bash`, `read`, `write`, `edit`. Copy Pi's behavior where
   it is clear-cut. All configured tool capabilities are available to the model up front,
   using one selected surface per tool, not every variant (see 3). Dynamic tool discovery and
   tool/skill pruning come later, not in this initial implementation
   (owner clarification 2026-11-10).
3. **One implementation, per-family surfaces.** Models carry model-family metadata. A tool has
   one implementation; core tools (`read`, `write`, `edit`, possibly `bash`) may own an opt-in,
   lightweight surface per family (name, schema, description, argument/result translation) so
   each family gets the shape it was trained on. When introduced, a model-family alias replaces
   the generic tool's schema and model-facing surface; do not expose both. Without an alias,
   use the generic surface. Bespoke tools have one simple, well-documented surface. Context
   pruning must account for surfaces. Implemented as separate tool classes per name; see 13.
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
      public abstract ToolRelevance RelevantWhen { get; }
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
15. **Model metadata** is Urd-owned and saved per provider connection/model: family
    (OpenAI, Anthropic, Google, Other) and whether the model supports images. Image support
    is explicit, not guessed from model names; unconfigured models default to Other/text-only.
    Each run freezes these settings in its model selection (owner clarification 2026-11-10).
16. **Context overflow** is handled by automatic history compaction (summarizing older
    conversation), which is distinct from tool-result pruning (see ARCHITECTURE.md). Compaction
    is the next design topic after tools; tools are not ready for normal use without it.
17. **Background on every tool** was re-examined after review and kept: every tool may run in
    the background, including future tools.
18. **Terminal provider failure ends owned work** (owner review 2026-11-10). If a model request
    fails terminally while background calls are pending, cancel those calls and join their
    cleanup and result saves before settling the run. Their completion does not restart the
    agent. Keep recorded results and acknowledge possible partial effects; cancellation is not
    rollback. Processes deliberately left running by already-completed bash calls remain
    chat-owned and unaffected. The already-approved context-overflow compaction/retry is a
    handled recovery path, not terminal failure; if that recovery fails, cancel owned work.
19. **Completion origin is not a provider role** (owner review 2026-11-10). A background
    completion remains a tool-origin event in storage, transcript and model context, never a
    human submission. The provider adapter may carry its labelled envelope and images through
    the ordinary `user` input channel. Preserve origin, call identity and untrusted-result
    labelling; do not promote tool output to `developer` instructions merely to avoid the word
    `user` on the wire. Qualify this mapping per supported provider route.
20. **Tool relevance** (owner review 2026-11-11). Every tool explicitly declares typed
    `RelevantWhen` metadata, separate from its agent-facing `Description`.
    `ToolRelevance.Always` declares unconditional inclusion (all six current tools);
    `ToolRelevance.When(guidance)` supplies immutable, nonblank relevance guidance for
    future pruning and dynamic discovery. This adds metadata only: no selector is
    implemented, and all configured tools remain available to the model up front.
21. **Tool-row presentation** (owner review 2026-11-12).
    - One fixed-height, single-line row per call: tool icon · major · minor, plus
      right-aligned elapsed time shown only from 5 s, in whole units (`7s`, `2m 13s`).
      Minor text never repeats the major part and is truncated at its end. Bash major is
      the first real command (skipping `cd …&&`, assignments and wrappers); the dropped
      prefix is visible only on inspection. Unknown/MCP tools fall back to name · compact
      arguments. File rows show the name only; identical names are not disambiguated.
    - A running call's icon slowly fades in and out (about a 2.4 s cycle; steady under
      reduced motion). A failed call (including non-zero exit) has a danger-coloured icon.
      Stopped, interrupted or never-run calls look like finished calls.
    - Calls from one response with two or more adjacent calls share a subtle left guide
      with `┌ ├ └` connectors; each row draws its own segment so partially loaded
      batches join.
    - A background call keeps its start row (background glyph; live time while pending)
      and gets a separate returned row where the model received the result: return
      glyph, muted major, the call's minor, total time.
    - The icon opens an interactive hover card with the model-facing input and output
      JSON (scroll, select, one Copy). It loads lazily, displays a bounded preview and
      copies the full payload.
    - File names show their resolved absolute path on hover. A shared context menu
      offers **Copy path** and **Reveal in Explorer** only.
    - Labels come from a backend display module, `source/backend/tool-call-display/`,
      beside `markdown-rendering/`, not the harness or tool classes. Full arguments
      never ship with rows. Expandable details are deferred; keep row parts modular so
      they can be added later.
    - **Data path** (owner review 2026-11-12, after three review rounds):
      - When a response's calls are authorised, the harness also stores
        `execution_tools.arguments_excerpt`: a generic, tool-agnostic JSON excerpt of the
        arguments, always ≤ 4 KiB including keys, escaping and markers. Top-level scalars
        are admitted first in source order (strings ≤ 2 KiB whole, longer ones become
        `{"$truncated": <256-char prefix>, "length": n}`), then top-level containers
        recursively (nested strings ≤ 256 chars, arrays ≤ 16 items plus a
        `{"$omittedItems": n}` marker), stopping at the first member that would overflow.
        Keys over 128 bytes are never admitted; omitted object members are counted in a final
        `"$omitted": n` member of that object. Source keys starting with `$` gain one leading
        `$` in the excerpt, keeping literal argument data distinct from generated markers;
        labels remove that extra prefix when displaying source keys.
        Window reads therefore stay bounded regardless of argument size.
      - History and position reads capture descriptors and the excerpts of their tool
        rows in the same storage transaction. The display module derives labels after
        the locks are released. Excerpts never reach the browser.
      - A `ToolCallLabel` has `Icon`, `Title`, `Detail` and `Path`. A truncated or
        omitted field is never treated as complete: a truncated path gives a name-only
        row without path actions. Paths are resolved lexically against the session
        working folder (sharing the tools' Windows path normalisation), without disk
        access; Reveal checks existence only when invoked.
      - Tool descriptors gain `background`, `batchedWithPrevious` and `batchedWithNext`.
        The batch facts are derived on read (the adjacent transcript entry is a tool call
        from the same response), not stored, so text between calls breaks the guide.
      - Responses that carry descriptors (history pages and position windows) carry
        `toolLabels` keyed by `requestId/callId`; the browser stores each label with its
        descriptor.
      - The hover card reads a `ToolCallTranscript` (model-facing input and output, as
        the model context presents them, including background placeholders and the
        returned-result envelope) on demand. The display is a bounded preview; Copy
        fetches the full text. Image handling follows the first attempted request whose context
        retained the result, using its frozen selection and compaction boundary. Before any
        receiver exists, inspection keeps image placeholders. Images never include inline bytes.
      - The returned row marks where the result entered model history. If the session
        settles before another request, the model has not yet read it; accepted.

## Open

- Class shapes are approved; provider-result encoding and process-lifetime mechanics still
  require the qualifications below. Do not invent another framework to close those seams.
- Compaction class shapes and storage (direction is already approved).

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
public sealed record ModelSettings(
  Guid ConnectionId, string ModelId, ModelFamily Family, bool SupportsImages);
public sealed record LlmSelection(
  ProviderConnection Connection, string ModelId, string? ReasoningEffort,
  ModelFamily Family, bool SupportsImages);
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
    Task completion, CancellationTokenSource cancellation, Func<Exception, Task>? onFailure = null);
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
internal sealed class BashTool(ChatProcesses processes, GitBash bash,
  IReadOnlyDictionary<string, string> environment) : AgentTool { ... }  // bash(command, timeout?)
```

**Waiting, completion delivery and Stop** (existing `SessionBase`).

```csharp
internal Task BackgroundToolCompletedAsync(Guid executionId, Exception? failure = null);
private Task<LlmRequest?> AdvanceExecutionAsync(SessionExecution execution,
  TaskCompletionSource<SessionExecutionResult> settlement, ExecutionOutcome outcome);
internal Task StopExecutionAsync();
private string GetStatus(SessionSnapshot snapshot);  // gains "waiting"
```
- Pending background calls with no next turn: status `waiting`, Stop available. A completion or
  user input wakes the run. Context preparation is the hook for later compaction.
- A background result reaches the model after all direct results as a labelled tool-completion
  envelope (origin, call identity, outcome, result marked as untrusted data), never as a human
  submission. The transcript shows it as its own row type. Provider wire roles may differ from
  Urd authorship; item 19 permits the ordinary input channel without changing the event's origin.
- Stop cancels the run's provider, foreground and background work and waits for processes and
  saves to finish. Processes left by earlier `&` commands survive. Hiding a tab stops nothing.

**Storage and crash recovery.** No new types.

```csharp
internal bool TryStart(Guid sessionId, Guid executionId, long revision,
  Guid requestId, string callId, bool background);                       // ToolStorage
internal bool Complete(Guid sessionId, Guid executionId, long revision,
  Guid requestId, LlmToolResult result);                                 // ToolStorage
public void RecoverInterruptedExecutions();                              // UrdStore → SessionExecutionStorage
```
- A background call is a saved call with `background = true`; its placeholder is generated from
  that flag, not stored. `Complete` saves a background result and its completion message in one
  transaction; the next-request claim includes waiting completions after direct results.
- Startup marks calls running at crash time "interrupted, possibly partly applied"; nothing re-runs.

**Background integration details (locally qualified 2026-11-11).** Schema 14 stores
completion identity separately from its recipient request and transcript publication. Started
background calls always reconstruct the same direct placeholder; completions are claimed before
steering and published only after streaming closes. Never-attempted recipient claims are released
on Stop/recovery; attempted history remains fixed. One guarded driver rechecks durable readiness
after input, completion or Stop signals and joins all remaining ownership outside the guard.
The registry alone cancels each background token, including a post-registration Stop recheck;
linked parent cancellation must not bypass its callback join. Its optional failure callback
fences admission and cancels the provider promptly, but never joins the registry itself.
The host explicitly invokes recovery under the data-directory lease, before creating sessions.
Opening a store alone neither recovers nor resumes work.

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
inherited pipes, spaces/Unicode, path forms and encoding. The first
[process probe](agent-tools-qualification.md#windows-process-ownership) passed its scoped
lifecycle checks: create suspended, assign both jobs, then resume; wait on the shell handle
for ordinary completion, but verify the call job is empty for cancellation cleanup. Retain
surviving call jobs under the chat owner without migrating descendants; reclaim empty jobs.
This is fixture evidence, not qualification of the integrated process implementation.

## Implementation-readiness review — 2026-11-10

At this historical checkpoint, read-only source review found integration gaps, not evidence
that the selected capabilities need replacing. The loop ran fake tools sequentially; its guarantees had to be adapted,
not simply removed. These are engineering checks against the agreed behavior, not extra
product scope or approval of new class shapes.

### Background delivery and settlement

- Keep the original placeholder stable when reconstructing context, even after completion.
  Append the actual completion separately, after a complete direct-result batch; never replace
  an already-sent placeholder or emit a second function result for the same call. Choose and
  qualify the provider encoding of the non-human completion envelope before integrating it.
- Claim completion messages into a specific next request durably. A result arriving during a
  provider request belongs to a later request, not a retroactive insertion into its input.
  Repeated notifications must not cause duplicate deliveries or competing drivers.
- A pending call remains owned through cleanup and the result/completion-message commit.
  Coordinate completion, waiting, next-request claims and settlement through the session gate;
  join tasks outside that gate. Stop/terminal provider failure prevents wakeup and joins saves before
  settlement. Save/cleanup failure keeps the run fenced rather than claiming successful Stop.
- `ToolStorage.Complete` currently rejects results after any newer request exists. Background
  calls need an exception to that request-order rule, not to session/run/revision identity or
  settlement checks. Stop must still permit saving actual results before settlement.
- Verify completion before placeholder delivery, during the next provider request, while
  waiting, and racing user input/Stop/provider failure. Exercise multiple completions,
  duplicate notifications and failed result commits with deterministic fake tools.

### Result fidelity, including images

- `LlmContextReader` currently restores JSON-only function results; `LlmProviderContext` rejects
  multimodal tool-result content. Adding `ToolOutput.Images` alone does not provide image input.
  Qualify text plus image through actual storage, context reconstruction and the supported
  provider adapter, for both direct results and background completion envelopes.
- Establish the source of `ModelAcceptsImages` and the explicit text-only-model response using
  the selected Pi behavior. Preserve saved images without turning base64 into model-facing
  text or silently claiming the model saw them. Reload must retain the original result.

### Crash recovery

- Recover the enclosing requests, inputs and run as well as unfinished tool calls, so a fresh
  user instruction can proceed after restart. Preserve completed results; distinguish calls
  never started from started calls with unknown effects. Recovery starts no model/tool work.
- Verify restart before a call starts, after effects but before result commit, and after a
  background completion commit but before delivery. Retain undelivered completion data for
  deliberate continuation; never repeat the effect to repair missing history.

### File ordering and process qualification

- Make same-path queue admission deterministic in emitted call order within a reply, including
  reads waiting on earlier mutations. Queue before asynchronous execution can reorder admission.
  This is not general dependency ordering; dependent commands still use separate replies/bash.
  Reads waiting on mutations are Urd's agreed behavior, not Pi's mutation-only queue behavior.
- Test path aliases, cancellation while queued and concurrent distinct files. The queue does
  not coordinate bash or outside editors; do not present it as filesystem-wide isolation.
- Qualify the nested-job mechanism before treating `LeaveBackgroundProcesses` as proven.
  Specify the ownership handoff on shell exit and retain chat ownership of survivors; timeout
  or cancellation of an unfinished call must kill/join that call's tree, not earlier survivors.
  A nonzero shell exit is still an exit, not proof that its descendants stopped.
- The fixture must prove backend-crash cleanup and that inherited output pipes do not keep a
  completed shell call alive. Do not assume descendants can be moved between Windows jobs.

**Delivery gates:** qualify process ownership and provider result encoding first; then integrate
foreground tools/parallel dispatch and their storage checks; then background delivery, waiting
and recovery. This names the foreground and background phases referred to by the compaction
plan; it does not defer the selected background capability. Compaction follows the background
phase. Tool-row presentation and compaction remain gates for normal-use readiness.
