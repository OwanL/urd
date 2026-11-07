# Urd architecture — visual audit map

> **Historical snapshot — superseded for current naming and structure.** See
> [Backend development](backend-development.md) and [Server development](server-development.md) for current contracts.
> Diagrams, counts and historical locations retain the captured state; source links point to current replacements.
> `StoredSessionSnapshot` has since merged into `SessionSnapshot`, the private `Settlement` is a named tuple,
> and shared `JsonText` validation now lives in `StorageText`.

> **Working-tree snapshot · 7 November 2026, 08:03 UTC**<br>
> Base commit: `e5537192e8c8b5f9eea76266c8f8a6726868732f`, **plus uncommitted changes**.
> This describes inspected source, not a new design approval or a runtime qualification.
> Steering, tool execution and schema v4 were in progress at capture time.
> The captured application source subsequently matched commit `732c84f`; tests changed after capture.

[Open the offline visual guide](architecture-map.html) · [Architecture decisions](ARCHITECTURE.md) · [Project direction](../README.md)

## Start here

**Urd currently has two application projects:** a local HTTP Server and an ASP.NET-free Core.
Core owns session identity, input admission, model/tool execution and SQLite persistence.
There is no implemented browser UI or real provider integration in this snapshot.

Read **1 → 2 → 4** for the broad picture. Use **3, 5 and 6** to inspect implementation boundaries;
**7 and 8** explain lifetime guarantees and the most useful review questions.

| Term | Meaning in this code |
| --- | --- |
| Session | A saved conversation plus, when opened, one canonical live handle per manager. |
| Input receipt | The saved decision to accept or refuse one exact input command. Not a model answer. |
| Run | Work started by an accepted input; later steering inputs can join it. At most one unsettled run per session. |
| Request | One model call within a run. A run can contain multiple sequential requests. |
| Settlement | The saved whole-run outcome after owned work and cleanup finish. Not merely the last text chunk. |
| Fence | Refusal of new work when stopping or when ownership/persistence is uncertain. Not automatic recovery. |

**Diagram key:** arrows in overview/flow diagrams are labelled by their meaning. In class diagrams,
`*--` means owned lifetime, `-->` a retained reference, `..>` a dependency and `<|--` inheritance.
`+` denotes public, `~` internal. Members are deliberately selected, not complete API listings.
Method parameters and asynchronous return wrappers are omitted for readability.

## 1. System boundaries

**Read this as:** HTTP adapts commands; Core decides and executes; SQLite retains the facts.
Core's sessions, execution and storage responsibilities live in **one assembly**, not separate services.

```mermaid
flowchart LR
  caller["HTTP caller<br/>or test client"]
  server["Urd.Server<br/>LocalBrowserGate<br/>SessionRoutes"]
  core["Urd.Core<br/>sessions · execution<br/>storage"]
  db[("SQLite<br/>schema v4 · WAL")]
  fake["FakeChatClient<br/>owned by Server"]
  caller -->|loopback HTTP| server
  server -->|Core API| core
  core -->|one connection| db
  core -.->|borrowed IChatClient| fake
  classDef host fill:#edf3fa,stroke:#7895ad,color:#1d3343
  classDef domain fill:#eaf4ef,stroke:#82a896,color:#1c4738
  classDef data fill:#fff5df,stroke:#cbb789,color:#614b22
  class server,fake host
  class core domain
  class db data
```

- The project reference is **`Urd.Server → Urd.Core`**, never the reverse.
- `Program.RunServerAsync` owns the directory lease, store, client, manager and web application.
  It borrows none of them from an external dependency-injection container. These composition and ownership
  edges are kept in prose rather than the overview.
- HTTP disconnects do not cancel admitted work. Receipt/snapshot/content reads do accept request cancellation.
- Only the Server composition uses `FakeChatClient`. Core accepts a borrowed `IChatClient` and optional
  `AIFunction` objects; the tool-capable path is present in Core, not configured by the normal server startup.

**Source:** [Program.cs](../source/hosting/server/Program.cs), [SessionRoutes.cs](../source/hosting/server/SessionRoutes.cs),
[LocalBrowserGate.cs](../source/hosting/server/LocalBrowserGate.cs), [Core project](../source/backend/Urd.Core.csproj),
[Server project](../source/hosting/server/Urd.Server.csproj).

## 2. Session classes and ownership

**This is a session-lifetime overview, not the complete class map.** The newer
[complete class atlas](class-map.html) covers all 28 classes plus 26 record/enum/struct types,
with grouped diagrams and a full [source index](class-map.md#7-complete-type-index).

**Read this as:** the manager keeps identity stable; the session owns the run lifecycle.
A session is not a model request, and neither owns the shared store or client.

```mermaid
classDiagram
  direction TB
  class SessionManager {
    -sessions : Dictionary
    +CreatePrimaryAsync()
    +GetPrimaryAsync()
    +ShutdownSessionsAsync()
  }
  class SessionBase {
    <<abstract>>
    +Id : Guid
    -acceptingInput : bool
    +GetInputReceiptAsync()
    +ReadSnapshotAsync()
    +ReadMessageContentAsync()
    ~RequestStopAsync()
    ~WaitForSettlementAsync()
  }
  class PrimarySession {
    +SubmitUserInputAsync()
  }
  class SessionExecution {
    ~RunId : Guid
    ~RunRequestAsync()
    ~StopAsync()
    +Dispose()
  }
  SessionBase <|-- PrimarySession
  SessionManager "1" *-- "0..*" PrimarySession : canonical handles
  SessionBase "1" *-- "0..1" SessionExecution : current run
```

| Owner | Keeps / coordinates | Does not own |
| --- | --- | --- |
| `SessionManager` | Canonical handle dictionary; registry lock; shared shutdown outcome; snapshot of configured tool references. | Store/client disposal; model-stream lifetime. |
| `SessionBase` | Session lock; input fence; current execution; driver task and settlement promise. | Provider implementation; direct tool invocation. |
| `PrimarySession` | Public user-input entry point, delegating to base admission. | A second state machine. |
| `SessionExecution` | Run cancellation source; sequential request execution and tool batches. | Session lock; SQL; borrowed client/tool disposal. |

`PrimarySession` inherits the read surface from `SessionBase`. The manager and each
session have **separate `AsyncLock` instances**: one protects registry coordination, the other serializes
that session's state and guarded storage calls. The manager passes its borrowed store and client references,
plus a snapshot of borrowed tool references, to its sessions. Sessions pass the borrowed client and tools to
`SessionExecution`; none of these classes owns their disposal. The execution owns only its run cancellation
source and model/tool work lifetime.

`SubagentSession` is a design direction, **not an implemented class** in this snapshot.
Addressed Stop and settlement waiting exist internally, but are not public HTTP operations.

**Source:** [SessionManager.cs](../source/backend/core/sessions/SessionManager.cs),
[SessionBase.cs](../source/backend/core/sessions/SessionBase.cs),
[PrimarySession.cs](../source/backend/core/sessions/PrimarySession.cs),
[AsyncLock.cs](../source/backend/core/sessions/AsyncLock.cs).

## 3. Inside one model request

**Read this as:** execution owns each model request and its cleanup; accumulation interprets updates;
the writer batches text; the session remains the gateway to storage.

```mermaid
classDiagram
  direction LR
  class SessionExecution {
    ~RunRequestAsync()
    -SendRequestAsync()
    -InvokeToolAsync()
    -CleanUpAsync()
  }
  class LlmResponseAccumulator {
    ~ToolCalls : IReadOnlyList
    ~AppendAsync()
    ~FinishAsync()
    ~FreezeJson()
  }
  class LlmResponseWriter {
    ~FlushDelay : Task
    ~AppendTextAsync()
    ~FlushAsync()
    ~FinishResponseAsync()
  }
  SessionExecution *-- LlmResponseAccumulator : creates per request
  SessionExecution *-- LlmResponseWriter : creates per request
  LlmResponseAccumulator --> LlmResponseWriter : text and final marker
```

| Record | From → to | Meaning |
| --- | --- | --- |
| `LlmRequest` | `RunStorage` → session → execution | Claimed request ID and reconstructed `ChatMessage` context. |
| `LlmResponseChunk` | writer → execution callback → session → store | Response ID, chunk index, text, final flag. |
| `LlmRequestOutcome` | execution → session → store | Model request outcome plus authorized tool calls; not run settlement. |
| `LlmToolCall` / `LlmToolResult` | accumulator / invocation → store | Frozen JSON arguments/results and explicit call identity. |
| `ExecutionResult` | store → session waiters/read views | Saved whole-run outcome: Completed, Cancelled or Failed. |

For each request, `SessionExecution` creates one accumulator and writer. The writer's `saveChunkAsync`
callback returns through `SessionExecution` to `SessionBase` for guarded persistence. Execution borrows the
session, shared `IChatClient` and configured `AIFunction` references; it does not own or dispose them. These
callback and borrowed-reference edges are intentionally omitted from this focused view.

The writer saves chunks at **16 KiB** or a **100 ms flush deadline**; this is not an end-to-end latency guarantee.
The accumulator accepts text and complete function-call content, ignores usage content, and requires a supported
finish reason. It is **not a provider-native delta assembler**. Each response permits up to 16 tool calls;
arguments/results are individually bounded to 16 KiB JSON. Tool invocation is sequential within a batch.

**Source:** [SessionExecution.cs](../source/backend/core/execution/SessionExecution.cs),
[LlmResponseAccumulator.cs](../source/backend/core/execution/LlmResponseAccumulator.cs),
[LlmResponseWriter.cs](../source/backend/core/execution/LlmResponseWriter.cs),
[LlmRequest.cs](../source/backend/core/execution/LlmRequest.cs),
[LlmTools.cs](../source/backend/core/execution/LlmTools.cs).

## 4. Send, steering and continuation

**Read this as:** acceptance is durable before execution starts; steering waits for the next request.
This is the successful path, with refusal/retry branches compressed into the notes below.

### Admission: save the decision

Admission holds the session lock through one storage transaction. Accepted input saves membership and a
message index, creating a run if idle. A new run's driver starts after the lock is released;
receipt delivery can race that execution.

```mermaid
sequenceDiagram
  participant C as Caller
  participant S as Session
  participant DB as SessionStore
  C->>S: SubmitUserInputAsync(command)
  S->>DB: Admit and persist decision
  DB-->>S: Receipt + run ID if accepted
  S-->>C: Saved receipt, not an answer
```

### Execution: repeat at a safe boundary

This view compresses the ordinary request path. It includes cleanup and persistence guards, not just model
streaming; model and tool work are outside the session lock. Persistence callbacks and request-boundary
claim/settlement decisions enter the lock. Only successful work with another claimable request loops back.

```mermaid
flowchart LR
  model["Model request<br/>stream · clean up · save outcome"]
  tools["Tools<br/>persist start · invoke · persist result"]
  boundary["Session-lock boundary<br/>more work?"]
  settle["Settle<br/>save · dispose · publish"]
  model -->|authorized calls| tools
  model -->|no calls| boundary
  tools --> boundary
  boundary -->|yes · claim next request| model
  boundary -->|no| settle
```

- An exact retry returns the original receipt without another execution. Reusing an input ID with changed
  contents throws `InputConflictException`. Accepted **and refused** decisions are retained.
- In this WIP snapshot, distinct busy input can join the locally owned run. Admission allows at most
  **8 nonterminal inputs** per run, including inputs already assigned to an unfinished request.
  Stopping, stale revision, capacity and an unowned active run can still cause refusal.
- Pending inputs are claimed together at the next request boundary. Tool results precede the next
  request's newly claimed user messages in reconstructed context. A text-only response can also be
  followed by a new request if steering arrived before settlement.
- Admission and the final pending-work check share the session lock: input cannot slip between the
  check and settlement. An input arriving after settlement starts a new run instead.
- The store rebuilds model context from claimed inputs, saved assistant text, calls and results.
  Visible admission order and model-delivery order are therefore intentionally different views.

**Source:** [InputStorage.cs](../source/backend/core/storage/UserMessageStorage.cs),
[RunStorage.cs](../source/backend/core/storage/SessionExecutionStorage.cs),
[RequestContextReader.cs](../source/backend/core/storage/LlmContextReader.cs),
[ToolStorage.cs](../source/backend/core/storage/ToolStorage.cs),
[SessionBase.cs](../source/backend/core/sessions/SessionBase.cs).

## 5. Persistence and read boundaries

**Read this as:** several small storage classes share one connection and one database lock.
The grouped operation boxes below are visual groupings, **not additional classes**.

```mermaid
flowchart LR
  store["SessionStore"]
  writes["Write components<br/>SessionStorage · InputStorage<br/>RunStorage · OutputStorage · ToolStorage"]
  reads["Read components<br/>SnapshotReader · MessageContentReader"]
  database["Shared Database<br/>one monitor · one SqliteConnection<br/>owns transaction lifetimes"]
  sqlite[("SQLite · WAL<br/>foreign keys ON · synchronous NORMAL")]
  store -->|owns| writes
  store -->|owns| reads
  writes -->|write| database
  reads -->|read| database
  database -->|SQL| sqlite
```

| Operation | Atomic boundary / read budget |
| --- | --- |
| Input admission | Receipt + run association + delivery membership + user-message index in one write transaction. |
| Claim next request | New request + pending-input assignment + context reconstruction in one write transaction. |
| Stream output | Each immutable UTF-8 chunk and its request/message bookkeeping commit together. |
| Complete request | Request outcome, assigned input outcomes and authorized tool-call rows commit together. |
| Tool start / result | Separate durable transactions before and after invocation; external effects are not a SQLite transaction. |
| Snapshot | One committed read view, merged with the live session fence. Up to 60 recent messages, 256 UTF-16 units per preview. |
| Full content page | Up to 16 KiB UTF-8, examining at most 16 segments. Cursor is segment index + byte offset, scalar-safe. |

`SnapshotReader` reuses `MessageContentReader` to make previews. Neither read starts execution.
During a request claim, `RunStorage` uses `RequestContextReader` to reconstruct context in the same write
transaction. On `Open`, `DatabaseSchema` initializes or migrates the schema to effective v4 before normal use.
`HasMore = false` means the current committed tail, not necessarily final output.
`IsClosed`, `FinalOutputCommitted` and saved run settlement answer different questions.
Snapshots expose user/assistant text previews; they are not a complete tool-call/result inspection API.
Older-history navigation is not implemented even though stored context can include older history.

The database lock serializes **all operations through this store**, including reads across different sessions.
WAL does not make this single connection execute parallel queries. Model and tool work run outside session/database
locks, but context reconstruction happens inside the claim transaction and session lock.

**Source:** [SessionStore.cs](../source/backend/core/storage/SessionStore.cs),
[Database.cs](../source/backend/core/storage/Database.cs),
[SnapshotReader.cs](../source/backend/core/storage/SnapshotReader.cs),
[MessageContentReader.cs](../source/backend/core/storage/MessageContentReader.cs),
[Messages.cs](../source/backend/core/sessions/Messages.cs).

## 6. Durable data model

**Read this as:** input decisions, delivery membership, model attempts and run settlement are different facts.
These are **selected relationship maps of effective schema v4**, not the old single-request schema or a full DDL dump.
Crow's foot = many; circle = optional; double bar = exactly one. Lines represent declared foreign keys.
Some direct session/run and role-conditional message edges are omitted for readability and listed below.

### Admission and delivery

```mermaid
erDiagram
  direction LR
  sessions ||--o{ input_decisions : retains
  input_decisions ||--o| execution_runs : originates
  input_decisions ||--o| execution_inputs : membership
  execution_runs ||--o{ execution_requests : contains
  execution_runs ||--o{ execution_inputs : receives
  execution_requests |o--o{ execution_inputs : claims
```

### Output and history

```mermaid
erDiagram
  direction LR
  execution_requests ||--o{ execution_output : stores_chunks
  execution_requests ||--o{ execution_tools : authorizes
  execution_requests |o--o{ execution_messages : assistant_message
```

| Table | Identity / purpose |
| --- | --- |
| `sessions` | `session_id` PK; current history revision. |
| `input_decisions` | `(session_id, input_id)` PK; original command bytes, Accepted/Refused decision and run/refusal data. |
| `execution_runs` | `ordinal` PK, unique `run_id`; originating input, stopping flag and nullable terminal outcome. |
| `execution_inputs` | `ordinal` PK; unique `(session_id, input_id)`; run membership, nullable claimed request and delivery outcome. |
| `execution_requests` | `request_id` PK; unique `(run_id, request_index)`; output identity, final marker and request outcome. |
| `execution_output` | `(request_id, batch_index)` PK; immutable bounded UTF-8 chunks and final flag. |
| `execution_tools` | `(request_id, call_id)` PK; call order/name/arguments, started flag, outcome and saved result/error. |
| `execution_messages` | `ordinal` PK; unique `(run_id, role, message_id)`; ordered user/assistant index for read views. |

**Important constraints and omissions:**

- Partial unique indexes permit at most **one unsettled run per session** and **one unfinished request per run**.
- Every run references its originating input decision through `(session_id, input_id)`;
  steering decisions have membership in that run without originating another run.
- `input_decisions.run_id` is a saved reference **without a declared foreign key**.
  `execution_requests.output_id` likewise is not a foreign key.
- A message row references an input decision for user content **or** a request for assistant content,
  enforced by a role check. The optional input-decision → message edge is omitted from the diagram.
- Other omitted direct FKs: sessions → runs/inputs/messages and runs → tools/messages.
  Separate FKs do not by themselves enforce that all duplicated session/run/request IDs agree;
  application writes preserve that consistency.
- The v4 migration rebuilds requests/output, adds input/message/tool tables and backfills older data atomically.
  Reading only the earlier creation DDL would incorrectly imply one request per run.

**Source:** [DatabaseSchema.cs](../source/backend/core/storage/DatabaseSchema.cs), especially
`EnsureSteeringSchema` for the effective v4 layout.

## 7. Shutdown and the lifetime barrier

**Read this as:** stopping is a request; disposal waits for successful draining.
The host closes HTTP admission before beginning the two drain operations.

```mermaid
flowchart LR
  gate["Close served origin<br/>HTTP gate refuses new requests"]
  http["HTTP drain<br/>StopAsync without cleanup deadline"]
  core["Core drain<br/>fence all handles · stop · join · settle"]
  success{"Both drains<br/>succeeded?"}
  dispose["Dispose app, client and store<br/>release directory lease"]
  failure["Report failure<br/>no forced disposal beneath uncertain work"]
  gate --> http
  gate --> core
  http --> success
  core --> success
  success -->|yes| dispose
  success -->|no| failure
```

The HTTP and Core drain branches run in parallel after the served origin is cleared. Core fences manager
creation/lookup and every held session before starting any Stop callbacks, then joins callbacks, model/tool
work and stream cleanup before saving settlement when state is certain. If a Stop write fails for an active
execution, local cancellation still starts, but the failed Core drain prevents host disposal. A caller cancelling
its shutdown wait does not cancel the shared shutdown operation. Stop callbacks may reenter session
reads/admission and are joined outside the session lock. A cleanup or uncertain persistence failure fences
the session rather than silently replaying work. Reopening a database preserves orphan runs; it does **not**
recover them or restart execution.

**Source:** [Program.cs](../source/hosting/server/Program.cs),
[SessionManager.cs](../source/backend/core/sessions/SessionManager.cs),
[SessionBase.cs](../source/backend/core/sessions/SessionBase.cs),
[SessionExecution.cs](../source/backend/core/execution/SessionExecution.cs).

## 8. Where to focus the architecture audit

These are **review questions backed by source**, not a claim that each tradeoff is a bug.
No refactor is proposed or authorized by this guide.

| Priority | Observation | Useful question / next evidence |
| --- | --- | --- |
| First | `RunStorage.ClaimNextRequest` reconstructs all eligible current-revision context through `RequestContextReader`, inside a write transaction and the shared database lock. UI reads are bounded; model context is not comparably bounded. | How does a long conversation affect memory and other sessions' read/write latency? Measure this boundary before adding concurrency machinery or a context cache. |
| First | Cleanup/save uncertainty retains a fenced run; startup has no recovery operation. Tool effects can happen between the durable start marker and saved result. | What explicit recovery or abandonment operation will make a conversation usable again without replaying unknown effects? This is an unfinished product path, not a reason to weaken fencing. |
| Next | `SessionBase` coordinates admission, request progression, persistence callbacks, cancellation and settlement. `SessionExecution` calls back into it. | Can these lifetimes still be understood locally as capabilities grow? Preserve one lifecycle owner; split only responsibilities with a genuinely independent lifetime. |
| Next | `SessionManager` keeps every opened handle; there is no eviction/close path. Client and tool instances are shared across sessions. | What is the measured cost of many idle handles, and do real clients/tools support concurrent sessions? Do not infer resource safety from the fake client alone. |
| Next | Delivery order differs from visible message order; tool facts are saved but not fully exposed by the current text read surface. | What should the first UI show about pending steering, tool activity and uncertain effects? A receipt or final text marker must not masquerade as completed work. |

### Present versus planned

| Present in inspected source | Not implemented / not qualified by this map |
| --- | --- |
| Two-project Server → Core dependency; local HTTP gate and five session operations. | Browser UI, desktop/VS Code hosts, LAN/Tailscale access. |
| Canonical primary sessions; durable receipts; committed snapshot/content reads; shutdown fencing. | `SubagentSession`, assignments, wakeups, session listing, public addressed Stop, startup recovery. |
| WIP steering, multiple requests per run, sequential configured tool invocation and schema v4. | Real provider adapters, native streaming/tool fidelity, normal-server tool configuration, a complete explicit follow-up queue policy. |
| Bounded text batching and bounded read pages. | Bounded/compacted model context, long-session/fan-out performance qualification. |
| Shared `IChatClient` abstraction and SQLite implementation. | MCP integration, guidance loading, usage accounting and the wider planned harness. |

### Evidence and maintenance

This map was checked against a frozen copy of the working tree, including untracked source files.
The application source was compared with the subsequent `732c84f` checkpoint and matched; three test/support
files changed after capture. The [backend verification record](backend-development.md) was subsequently updated
in `4ea3d1f` to report 259 passing Release tests for that checkpoint. This map does not independently rerun them.
Consult the [backend](backend-development.md), [server](server-development.md) and design records for scoped
approvals, verification and implementation limits. Older statements that steering/tools are absent describe
previous checkpoints, not this source snapshot. Source links above point to the **live checkout** and can move.

No application code, application data or existing working-tree edits were changed for this guide.
Validation covers diagram rendering, document links and independent source-to-diagram review;
application builds/tests and live-provider/performance validation are not claimed.

This Markdown is the editable diagram source (Mermaid). The HTML companion is a static, offline rendering
of the same snapshot, with compact views, expandable notes, full-size diagram controls and printable layout;
it makes no network requests. Shared dependencies and detailed constraints live beside the focused diagrams
rather than adding more crossing lines.
It is not live-generated from C#. When updating the map, recheck ownership, cardinalities and transaction
boundaries against source, update the snapshot label, render the Mermaid blocks again, and regenerate
both views together. Mermaid **11.12.2** was used for this rendering.
