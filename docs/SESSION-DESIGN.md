# Session foundation: owner-reviewed working sketch

**2026-11-04 — design, not an implementation or a frozen class API.** This
stream owns only this report. [ARCHITECTURE.md](ARCHITECTURE.md) remains the
requirements authority. No production scaffold, dependency change or Pie data
inspection was performed. One backend process remains a working default, not
an additional decision made here.

## Owner input and decision boundary

The owner participated before the structure was settled:

- **Classes before files:** “class structure somewhat decides file system
  structure.” An initial folder-first proposal was premature.
- **Small session hierarchy, shared logic:** the owner wanted session kinds,
  relationships and useful OO reuse considered together, including titles,
  compaction and pruning, “Without going overboard.” The two-kind tree below
  received **rough approval**, not approval of every method or abstraction.
- **Names:** `PrimarySession` was explicitly selected after rejecting
  `ChatSession`/`ConversationSession`; `SubagentSession` was considered clear.
  The owner preferred `SessionBase` or visibly explicit base-type naming.
- **Auxiliary operations:** roughly accepted separate title/compaction/pruning
  operations sharing LLM-call behaviour, rather than full session lifecycles.
  After rejecting the unclear `ModelRequestExecutor` name, the owner accepted
  using selected `IChatClient` directly if it does the required job. No extra
  first-party facade is mandated. Provider qualification still matters.
- **Execution separation:** explained and included in the rough structure;
  `SessionExecution` is a **working collaborator name**, not a separately
  ratified API. It is an owned temporary object, not a method or session subtype.
- **Independent-worker closure is unresolved.** Bounded children should close
  with their containing chat. For independent workers, the owner said probably
  not, or possibly yes with an active-child warning; concrete UI is needed before
  choosing. Human steering does not itself transfer ownership, as already agreed.
- **Project boundary selected after staged review; finer layout remains open.**
  The owner initially deferred the folder/project example as premature. After
  responsibility alignment, step 2 selected a revisable Core/Server split (§5).
  This is not approval of a complete file map or permission to scaffold it.

## 1. Three kinds of ownership

| Owner | Facts or resources | Lifetime |
| --- | --- | --- |
| **Durable records** | Session identity/kind/settings; ordered original history; LLM-context representation; assignments/results; explicit delayed follow-ups; usage totals; attachment references | SQLite/files under retention rules; survive backend restart. Not serialized live object graphs. |
| **Live session/execution objects** | Admission/mutation guard; current execution identity; LLM/tool work, cancellation, queued input and waits; bounded live stream state | Backend-owned. A session handle can be loaded lazily; an execution exists only while work is active. Neither needs its own thread/process. |
| **Client presentation** | Selected session, unsent drafts, finite page/detail caches, view/connection state | Client-owned where the approved simplicity/cost conditions permit. Never authority for whether an agent is running or an action occurred. |

A stable session ID is allocated independently of its mutable title. Browsing
history does not start an execution or require loading all history. The database
is durable truth; an in-memory session is a controlled handle/cache, not a second
independently writable history. Original readable history and the LLM's compacted
context are different views: compaction does not erase the former.

**Finished execution ≠ finished assignment ≠ closed session.** A worker can be
idle with an unreported assignment. A recorded result is an explicit outcome
claim for its requester to verify. Final closure removes conversation/tool
history, not merely a tab; snooze preserves it. Results still needed by another
open session survive the worker's closure.

## 2. Session subtree and composed collaborators

```text
abstract SessionBase                 shared identity/context access/run guards
├── PrimarySession                   ongoing; user- or agent-created SAME type
└── SubagentSession                  bounded task → result; caller-owned work

SessionBase ── owns 0..1 active ──> SessionExecution
                                      ├── uses request-only IChatClient
                                      └── uses ToolExecutor
TitleGenerator ─────┐
HistoryCompactor ───┼── use request-only IChatClient too
CatalogPruner ──────┘
```

**Proposed responsibility limits:**

- `SessionBase`: common identity/settings/history/context access and guarded
  start/stop/reporting mechanics. Do not put every feature in the base or invent
  unsupported virtual methods. Ordinary errors settle the affected operation.
- `PrimarySession`: ongoing input/editing, assignment participation, snooze and
  final-close rules. Human-created and independently delegated sessions can both
  be human-steered; that does not change their CLR type.
- `SubagentSession`: one direct caller/task/result relationship and caller-linked
  cancellation. It reuses execution and persistence without acquiring ongoing
  messaging, arbitrary assignments or follow-up scheduling merely by inheritance.
  Finished helper detail remains inspectable until its containing primary closes,
  except detail removed by an explicit history edit. Nested helpers retain the
  same containing-primary retention boundary.
- `SessionExecution`: the current async job, including LLM/tool cycle, pending
  steering, waits and cancellation. It reports progress/results through the
  session's guarded write path; it cannot append after an edit/close invalidates
  it. Settlement releases live resources, not retained session data. Long waits
  and provider calls do not hold a session mutation lock.
- `ToolExecutor`: resolves a complete call against current implementations and
  permissions, executes it, and returns the actual outcome. Historical exposure
  is not execution permission. Tools that coordinate sessions enter the same
  session command path; they do not mutate database records behind its back.
- **`IChatClient`, not another mandatory wrapper:** shared configured request
  mechanics and provider adapters are reusable by all callers. Concurrent calls
  are allowed; this is not a global serial queue. Caller-specific prompts,
  validation and fallback policy stay with the operation. The request-only
  client is not the tool loop. Title failure can use a snippet; compaction failure
  must preserve usable context; pruning protects core entries before selection.
  Cancellation is not swallowed as ordinary fallback. A request retry cannot
  replay completed tools. Stateful request/context data must not leak between
  concurrent consumers of a shared client.

**First-slice responsibility alignment (2026-11-04):** The owner first asked to
“Discuss the boundary first.” After a concrete send/edit/Stop walkthrough, they
answered: **“Makes sense. of course we should keep things open, i am sure things
may change at implementation time”.** The working boundary puts identity/current
execution, admission/Stop coordination and stale-update rejection in `SessionBase`;
`SessionExecution` owns temporary task/cancellation/LLM-tool work, while
`SessionStore` owns atomic durable writes and revision checks. Editing, assignment
and caller-specific policies stay with the appropriate concrete kind. “One run”
means one main execution, not a ban on concurrent title generation. This supports
a narrow shared base rather than another coordination helper **as a starting
point**, not frozen methods, mandatory lifecycle hooks or a prohibition on later
composition. Manager APIs, broader base scope and project/file layout are not
approved by this answer; implementation evidence may change the split. The
subsequent project-boundary decision is recorded separately in §5.

Auxiliary cancellation belongs to its caller: a title's naming attempt/session,
or the context operation invoking compaction/pruning. Context-changing work
participates in session admission/busy state; a concurrent title only publishes
through a guarded metadata update. No auxiliary-session/job framework is needed.

Only the two session kinds form an inheritance tree. `SessionExecution`, tool
execution and auxiliary operations are **used/owned**, not inherited. No
`UserAgent`, `DelegatedAgent`, LLM-specific or role-specific session subclasses
are justified. Execution Design reports the owner's preference to omit the full
optional MEAI loop for its modest probe-code saving; the recommendation is one
small Urd-owned cycle over `IChatClient`, not a different loop per kind. Production
code savings and final APIs remain unqualified; see
[EXECUTION-DESIGN.md](EXECUTION-DESIGN.md).

### Relationships are not all parenthood

Keep these facts distinct rather than building an ownership DAG:

- **Creator:** stable provenance and creator cleanup authority for an independent
  primary. It does not decide automatic cascade closure or transfer on human input.
- **Bounded caller/containing primary:** direct result/cancellation and history
  retention for a helper. This is a single-owner relationship, not arbitrary peers.
- **Assignment requester/worker:** fixed participants in durable work, independent
  of the worker's creator. Cross-family collaboration does not add lifetime owners.
- **Presentation grouping:** may group workers under their creator without making
  the renderer the owner of their lifetime. Exact grouping/relative addressing,
  detachment and independent-close UI are not selected.

Do not translate `CreatorId` into a cascading foreign key. Do not return title
inference as the identity-readiness barrier. No generic relationship framework
is needed: these are a few explicit IDs with different meanings.

### Owner decision (2026-11-07): Stop scope and settlement

**Required product contract, not a qualified implementation:** Main-session Stop
must cancel current work, including tools, bounded subagents and all descendants
of those subagents. No owned work may silently continue or produce a false
“Stopped” status. Separately opened independent worker chats continue, even if
the stopped session created them or a human has steered them.

Cancellation requested is not cleanup complete: retain stopping/fenced state
until owned work is confirmed stopped, with no overlapping replacement. Future
tool/child implementations must own and join nested work, forbid untracked
background work and reject late child admission after stopping begins. A token
or disposed handle is not confirmation that an external process has exited.

Stop is distinct from close/delete and recovery: it does not delete history,
undo completed effects or justify replay of uncertain work. The independent-close
owner gate and creator-cleanup authority remain unchanged. Current runtime scope
is primary-session/LLM work and configured fake `AIFunction`s only. This is
not implementation or qualification of bounded-subagent/process-tree cancellation;
no future classes or frameworks are promised.

## 3. Durable facts and boundary rules

Logical facts below are **not a final schema or new class per row**:

| Fact | Required distinction |
| --- | --- |
| Session/history | Stable identity and kind; open/snoozed/closing status as needed; ordered message/detail identities; suffix-edit identity/revision; original history versus LLM context. Missing/closed targets reject mutations—append must never implicitly recreate them. |
| Admitted input/execution | Input identity and queued/delivered/interrupted status; correlation to the execution allowed to report progress. Transport receipt, LLM consumption and effect completion are not interchangeable. |
| Assignment/result | Task ID, requester, worker, accepted revision, state, explicit outcome/result. One nonterminal assignment per worker. Identical finish returns retained evidence; stale/conflicting finish cannot overwrite it. Persist before notification; notifications do not wake an idle requester by default. |
| Follow-up | Stable registration/target, intended due time, request and dispatch/uncertainty evidence. A never-dispatched due request is not an interrupted effect being replayed. |
| Retention | Compact usage after close; coordination facts while open participants/retained references need them; attachment references across sessions. No permanent conversation archive or unconditional infinite assignment retention. |

Use a narrow `SessionStore` with intent-specific transactions, not an ORM,
event-sourcing engine or interchangeable-store framework. A small session lookup/
creation coordinator (working name `SessionManager`) owns one active handle per
ID and cross-session admission; it does not absorb the LLM loop. Session
commands enforce rules, then store operations commit facts atomically. Exact
locking and method signatures remain implementation work.

Read windows, their cursor and history identity must describe one consistent
snapshot. An append is not the same invalidation as suffix replacement; assignment
revision is not a transport sequence. Shared SQLite validation reproduced a
count/window cursor race, freely overwritten task results and append-after-delete
recreation in the reduced experimental contract. **Do not copy that contract as
Urd's domain model**; see [SQLITE-VALIDATION.md](SQLITE-VALIDATION.md).

Preserve provider-specific context/call IDs and exposed-definition order where
needed. `IChatClient` is not a durable serialization format: default serialization
of provider `RawRepresentation` is insufficient. This follows the
[discovery](DISCOVERY-DESIGN.md) and [provider](PROVIDER-QUALIFICATION.md) findings.
Execution Design's newer owner alignment narrows the earlier discovery proposal:
use qualified native appended definitions without an upfront invocation wrapper;
otherwise include a stable invocation wrapper from the initial catalog for
recovered tools. Retain AI pruning and one current-authority dispatch path. This
**tool-invocation wrapper** is unrelated to the rejected extra LLM-client facade.
Provider-specific terminal success must be established before tool dispatch:
complete-looking arguments alone can survive a failed/incomplete/truncated response.

## 4. Walkthrough: where the sketch must hold

These are proposed mechanics checked against recorded requirements, not claims
of working Urd behaviour. The bounded executable follow-up in §7 tests a subset;
it does not qualify this entire walkthrough.

| Flow | Ownership and consequential boundary |
| --- | --- |
| **Submit / stream** | API routes a command to the primary; admission saves the accepted input, then starts one execution. Provider/tool events carry its identity through guarded updates. Bounded live deltas may precede batch persistence, but provisional output is not called durable. A title candidate cannot block stable identity or overwrite a newer manual title. |
| **Input while busy** | Ordinary user input defaults to durable steering at the next safe request boundary: after a tool-result batch, or after a no-tool response finishes. Inputs stay separate and ordered; they never rewrite a claimed request. This owner clarification supersedes timing-inferred preflight follow-up. Explicit after-run queueing remains separate work. Notes do not start idle work; assignments cannot silently merge into a busy worker. |
| **Edit pending input** | Compare the exact pending input/delivery identity before replacement. Reject a delivery race; preserve whether input is steering or follow-up. Do not clear unrelated notes/results as a shortcut. No transcript suffix mutation or tool replay is involved. |
| **Edit earlier prompt** | Stop and await affected execution; block new admission; atomically replace the suffix and invalidate stale execution/detail/context identities. Removed conversation does not roll back files/network actions or erase separately needed assignment evidence. Failure after committed replacement cannot restore the old tail on a guess. Then start replacement input deliberately. |
| **Cancel / Stop** | Cancel active/queued work, tools, bounded subagents and all their descendants, plus applicable auxiliary work; join owned nested work and reject late child admission. Cancellation requested is not cleanup complete: retain stopping/fenced state until confirmed stopped, with no silent background work, false “Stopped” or overlapping replacement. A token/disposed object is not proof an external process died. Separately opened independent worker chats continue regardless of creator or human steering. Stop is not close/delete or recovery (§2 owner decision). |
| **Bounded child** | Create a `SubagentSession` with stable caller identity and bucket selection; share the execution code, not its LLM context. Return one direct outcome. Release live execution at finish; retain lazy-readable child messages/tool detail with the containing chat. |
| **Independent worker / human steering** | Create another `PrimarySession`, then admit explicit durable work from its requester. Human input uses that same session; it neither changes type nor cancels the creator's cleanup authority. Idle without an explicit finish is awaiting-result, not success. Preserve default running-worker cleanup skip and instructions to inspect human use/results. |
| **Parent final close** | Fence admission, stop bounded owned work, then delete the containing conversation/helper detail and cancel follow-ups targeting it. Retain needed assignment/results independently. Independent-worker cascade/survival is an owner gate: this sketch must not implement either implicitly. Self-close must not wait for its own execution to die before returning acceptance; cleanup completion is separate. |
| **Browser disconnect** | Dispose that client's subscriptions, not the session or execution. Reconnect reads bounded durable/live state and command evidence; never blindly resubmits a command with a lost acknowledgement. Unsent drafts are not accepted work. |
| **Backend restart** | Reconstruct saved sessions and assignments/results; no live execution objects survive. Mark interrupted/uncertain work honestly. Recover completed results without invoking tools again; missing results do not prove effects never happened. An unresolved effect needs deliberate resolution, not a fake success or transcript-repair rerun. |
| **Due follow-up** | A never-dispatched saved request for an open target runs automatically when due/after restart, through ordinary admission, not a parallel execution. Claim/admission must be correlated atomically so one registration cannot become two inputs. Previously dispatched/ambiguous work is not automatically redelivered. Closed targets are discarded, never reopened. |
| **Snooze / restore** | Idle-only admission, no pending work bypass. Preserve history and deadline; restore visibility without focus or an AI turn. Keep existing pending-work/follow-up safeguards until their interaction is explicitly designed; snooze is not a hidden execution pause. |

A small durable dispatch marker/transaction can conservatively leave ambiguous
work for resolution. This is not a promise of exactly-once external effects,
a workflow-replay engine, per-token fsync, or a specified loss-time budget.

## 5. Core/Server project boundary — selected, revisable

**Step 2 owner choice (2026-11-04): “Yes — Core + Server”.** This followed a
request to discuss the single-project trade-off, not a silent promotion of the
previously deferred example. The concrete distinction: a single backend project
allows session code to take an ASP.NET `HttpContext` unless review/tests reject it;
a Core project without that dependency rejects the coupling at compile time.
The owner chose the narrow enforced boundary after discussion. The step 1 request
to keep the design open to implementation-time changes still applies.

**Selected:** `Urd.Server → Urd.Core`, never the reverse. Core contains session,
execution and storage logic without an ASP.NET dependency; Server owns hosting,
HTTP entrypoints and composition. This adds one project/reference and a boundary
API, **not** a published SDK, interfaces for every class, additional processes or
frozen methods. Both projects can evolve together. One project remains simpler
plumbing, but is not the selected starting point.

**Historical coarse source mapping:** the later [application plan](APPLICATION-PLAN.md)
now owns the rough layout, and [experimental source has been archived](../README.md#experimental-source-archive).
The earlier sketch below did not approve every filename, frontend structure or
test-project arrangement:

```text
src/Urd.Core/
  Sessions/                   SessionBase, PrimarySession, SessionExecution,
                              SessionManager — implemented behaviour, not stubs
  Storage/                    SessionStore and required durable facts
src/Urd.Server/               host entry/composition and actual HTTP handling
src/Urd.Web/                  shared TypeScript/Preact UI; UI-specific test source
                              (internal organization/tooling still open)
tests/Urd.Backend.Tests/      proposed shared backend behaviour/SQLite-flow tests
experiments/                  existing research harness, unchanged
```

For the known first primary-session flow, add only files with actual behaviour.
`SubagentSession`, title/compaction/pruning and provider/tool collaborators enter
when their feature needs code; do not pre-create empty future folders/classes.
This does not remove retained capabilities. Nor does splitting production code
require a test project per assembly or a frontend copy of the C# class hierarchy.

Within Core, sessions coordinate execution and guarded store writes; execution
uses `IChatClient`/actual tool execution, never unrestricted history mutation.
Read-only history queries can use the store without starting an execution.
Server wires dependencies; session/storage code does not consume HTTP or UI state.
Transport, endpoint style, CSS, frontend state and build tools remain open.

That structure decision did not authorize moving existing research/user files or the
harness; the later owner-authorized experimental archive is recorded above. Step 3 must scope
SDK/build/package/formatting policy and commands deliberately: new root settings
must not silently change experimental pins or behaviour. Conventions, tooling,
package pins and detailed file/test placement await that review. This decision
authorizes **no scaffold, source generation, build or installation**.

## 6. Acceptance sequence and remaining gates

**Proposed first technical checkpoint, not normal-use product scope:** create
primaries with stable identities; submit/stream through a deterministic
`IChatClient`; save/reopen a bounded recent window; switch another session while
streaming; disconnect/reconnect without cancelling or duplicating work; Stop
settles before another execution starts. The smaller two-chat/two-client draft
excludes real providers/tools, delegation and edit/close UI. Its idle-only new
Send restriction remains **unselected** after the owner review in §8; steering
and follow-up input remain normal-use behavior, not deleted features.

Pending/earlier edits with delayed stale callbacks are a subsequent acceptance
step, not a reason to expand this first checkpoint. Use a real qualified provider
separately; a fake client proves control flow, not entitlement, provider terminal
semantics or native-context fidelity. Neither this sequence nor the existing
disposable fixtures authorizes production implementation.

**Foundation follow-ons before shipping the corresponding features:**

1. Bounded helper finishes, remains inspectable, and is cancelled/removed correctly
   with the containing chat; ordinary final response does not close a primary.
2. Independent worker receives human steering and assignments from a non-creator;
   explicit immutable result survives restart and worker close for its requester.
   Closing one participant does not purge still-needed attachments/results.
3. Kill before/after result recording: completed effects never replay; uncertain
   effects remain explicit. A due never-dispatched follow-up resumes once, whereas
   an ambiguous dispatch does not; a closed target stays closed.
4. Concurrent append/read/edit produces consistent cursors and no stale callbacks
   resurrect a closed ID. Measure actual click-to-visible content with large lazy
   details and background work, not SQL timings or a count-only window.

**Decisions likely to cause expensive rework:**

- Lifetime relationships and independent-primary close behaviour: requires a
  concrete grouping/close UI; no implicit ownership cascade while unresolved.
- Durable command/task/history identities and atomic mutation boundaries: settle
  before binding the browser and retaining real data. Include schema-version
  handling and reference-aware cleanup; the experimental schema is insufficient.
- Lossless provider/LLM-context representation and effect uncertainty: qualify
  early with the chosen route; neither a common interface nor retries solve it.
- How much behaviour truly belongs in `SessionBase`: test both kinds before
  expanding the base. Keep the working hierarchy small, not “set in stone” APIs.
- Core/Server is the selected working project boundary (§5); detailed module/file
  mapping, shared build-policy scope and test placement still need review. Neither
  the class sketch nor project choice authorizes production scaffolding.

**2026-11-05 process update:** the owner wants the first class structures, important
members and method signatures proposed for review before implementation; see the
[initial design gate](APPLICATION-PLAN.md#initial-implementation-owner-design-review-first).
This supersedes treating all initial API details as delegated implementation work.
After the relevant structure is accepted, routine helpers, local names, lock primitives
and batching details can follow evidence without a method-by-method questionnaire.
The disposable flow test below is bounded evidence, not approval to derive/scaffold
a production layout automatically. The base scope and untested flows remain open.

## 7. Disposable session-flow qualification

**Follow-up, 2026-11-04:** Three focused flow groups passed in a temp-only C#
fixture using request-only `IChatClient` (Abstractions 10.11.0), a deterministic
fake LLM and real Microsoft.Data.Sqlite 10.0.14 transactions. These are fixture
pins, not new dependency selections. Two serial runs—DLL-hosted and apphost—each
passed **42 assertions** (34 parent, 1 exiting child, 7 recovery child); the build
had zero warnings/errors. Independent source and final evidence review were used.

| Flow group | Established within the fixture |
| --- | --- |
| **Admission / stream / Stop** | One accepted run; a second admission rejected by both live handle and SQLite facts. Cancelling the synthetic browser reader left the LLM running and later output recorded. Stop remained “stopping” while fake LLM cleanup deliberately waited at a barrier; new admission and late output stayed blocked until it settled. A new run then worked; the old execution ID could not append. |
| **Earlier edit / close / restart** | Edit blocked admission, awaited the old execution, then atomically replaced the suffix and advanced history identity. Delayed callbacks could neither append nor publish; an injected precommit failure rolled back both history and revision. A wrong history revision failed even with the current run ID. Direct session close awaited owned work and removed history; stale append/admission could not resurrect the ID. |
| **Explicit assignment result** | A requester distinct from the named creator assigned work. Stream completion left no result; explicit resume advanced the accepted revision. Wrong-worker/stale finishes rejected; identical finishes returned retained evidence and conflicting finishes rejected. Closing the worker removed its conversation but retained the result for the other open requester, including after process restart. |

The parent launched its **own** child, which committed an accepted input/partial
output while the fake request remained active, then exited with code 17 without
graceful settlement. A fresh child reopened the database: committed edits/results
and closed identity survived; the active marker became explicitly interrupted,
old execution callbacks rejected and **zero LLM requests restarted**. This is
process-exit/reopen evidence after returned commits, **not** a crash during a
SQLite transaction, power-loss test or exactly-once external-effect guarantee.

**What the split helped—and did not settle:** A temporary execution could own LLM
cancellation independently of the reader, while narrow guarded commands and
intent-specific transactions protected durable facts. No extra LLM-client
facade, event bus or workflow engine was needed. The complexity did not disappear:
Stop/edit must coordinate settlement **without waiting under the mutation guard**;
history revision, execution ID and accepted assignment revision protect different
things. Checking only a cancellation token or only an in-memory base object is
insufficient. These tests do **not** place those guards in an approved `SessionBase`
API, select a lock primitive, instantiate both session kinds or validate inheritance.
Do not widen/freeze the base from this single-handle experiment.

**Deliberate limits:** One synthetic reader, no HTTP/reconnect protocol or stale
client-cache/UI clearing; no tool/native-envelope transport, bounded-child cascade,
independent-parent closure, simultaneous multi-process admission, attachments,
notification delivery, follow-ups, compaction or retention GC. Small commits per
synthetic chunk, closed tombstones and an interrupted admission gate are test
mechanisms—not selected batching, schema, permanent tombstone retention or resume
UX. Parent closure remains an owner gate; this test closes only an explicitly
addressed individual session. No new product or structural decision was made.
[Composed execution](EXECUTION-DESIGN.md) separately qualifies bounded native/tool
recording mechanics; combining that with these session guards remains untested.

**Reproduction/evidence:**
`%TEMP%/urd-foundation-streams-6268b517/session-flows-d0914b79/` retains source,
`README.md` commands, `build.log`, both run logs and database evidence. Runs used a
started exclusive `urd:heavy-local-work` lease, cached SDK/packages, no compiler
server and only owned sequential children. Children exited and no fixture process
remained; generated build output was removed after review. No live LLM/network
call, production source/schema, installation or activation was involved.

## 8. Resumed input/recovery review — 2026-11-05

**Owner outcome: prefer simplicity, not a selected recovery interface.** The
scenario was a publish command that had stopped, followed by a backend crash
before its result was saved; the service could not confirm whether publication
succeeded. The owner twice said the question was unclear, finally answering
**“I still dont really understand, whatever is simplest”.** Do not turn this
into claimed informed approval of either offered move-on/blocking control.

The **revisable working recommendation under that delegation** is: once old
owned work has actually stopped, keep the missing effect result marked unknown,
never automatically replay it, and permit a fresh instruction. Do not add a
chat-wide reconciliation-only lock or recovery wizard. Continuing does not mark
the old command successful, undo its effects or authorize repeating it. Exact
controls and LLM-context representation remain implementation work; this
review did not qualify them. The recommendation was stated to the owner, not
separately confirmed by them.

This is not permission to ignore possibly live work or ambiguous admission:
Stop/settlement and stale-write fences still apply. If a Send acknowledgement is
lost, the old request may still be accepted; retain/reconcile its identity rather
than minting another request or declaring rejection. Existing receipt lookup must
remain available while busy. A missing tool result and an unknown Send receipt
are different problems, not one universal retry button.

**First-checkpoint staging remains a proposal.** Asked whether Send could be
unavailable during a reply in the first internal fake-LLM test, the owner said
**“I still dont understand what you are trying to do”.** No idle-only approval
followed. The stream explained that developer checkpoint sequencing is not a
change to the finished product and withdrew the request to make that engineering
detail an owner choice. The §6 checkpoint is preparation, not a selected minimum
usable product or permission to scaffold it. If idle-only admission is used for
that checkpoint, reject new input before acceptance and preserve the draft;
never accept-and-drop or silently queue it. Normal-use mid-response input stays
retained, with preflight follow-up versus safe-boundary steering distinguished.

The proposed fake-text restart check keeps saved partial output explicitly
interrupted, loses only provisional output under the proposed process-crash
interpretation below, and makes no automatic LLM request. Deliberate new input
must not re-admit the old input. This narrower check does not decide all recovery
controls or weaken **automatic due work proven not to have started**. Existing
snooze safeguards, shared-result retention and final closed-history deletion stand.
Independent-worker closure still needs meaningful UI context; it does not block
this unrelated primary-chat checkpoint. No new closure mock is proposed.

**Storage wording proposal for coordinator integration, not new authority:**
clarify the current acknowledged-loss/identical-replay tension by distinguishing
live provisional output from returned committed database records. Candidate
wording: “For process crash/reopen, committed database records replay completely
and in order; live output not yet committed may be lost. Machine/system crash
or power loss is outside that process-crash guarantee.” This does not promise
attachment-file atomicity, exactly-once effects, per-token fsync, a loss-time
budget or qualification of every failure mode. It proposes no durability upgrade;
shared authority remains unchanged until the coordinator reconciles its wording.

**Evidence/limits:** reread the retained input/Stop/continuation behavior in Pie's
`STATE_CONTRACT.md` (Execution Ordering and Queued Follow-ups), the existing §7
fixture, Execution Design's recorded-effect/result-write failure and
[DURABLE-BOUNDARIES-EVIDENCE.md](DURABLE-BOUNDARIES-EVIDENCE.md), plus the TEMP
first-slice implementation/storage proposals. The results are existing bounded
evidence and source-grounded reasoning, not new execution. No probes rerun,
scheduler/cleanup/session code, schema, account access or build was added.
Client/coordinator were told both the simplicity delegation and the unanswered
staging question; peer notification is not approval. Focused independent read-only
review found no consequential correctness or authority issue in this follow-up.
No new runtime qualification is claimed.

## Evidence and limits

Read Urd's README/ARCHITECTURE and relevant LLM/discovery/storage research;
inspected Pie at **`cc82abb0f317f31341d3a6f12aa1545f6be1fbea`**, contracts and
bounded owning source only. Useful Pie anchors (paths in the Pie repository):

- `docs/contracts/AGENT-SESSION-CONTROL.md`: creator vs requester, non-waking
  notes, one active assignment, explicit immutable finish, human steering and
  cleanup. `harness/agent-processes/coordinator/session-tasks.ts:250–320` confirms
  finish/retirement mechanics. Urd deliberately replaces coordinator-lifetime
  retention, not copies that limitation.
- `request-handler-message.ts:278–352,1103–1187` under that coordinator:
  preflight follow-up versus streaming steering, pending-edit fences. Earlier
  edit ownership transition: `coordinator/server.ts:4045–4114`.
- `docs/contracts/STATE_CONTRACT.md`, Session Activity, Snooze, Cleanup and
  Snapshot Recovery: idle-only snooze, guarded settlement, lazy detail, original
  versus live state. Browser disconnect source:
  `application/hosts/browser/http/browser-server.ts:538–551`.
- `docs/contracts/DEFERRED-TRIGGERS.md`: durable deadline and ambiguous dispatch
  behaviour. Urd does **not** inherit restoration of closed targets, sidecar
  claims, multi-condition triggers or its cross-process machinery.
- `docs/contracts/SESSION-TITLES.md` and
  `harness/model-providers/title-generation/title-generator.ts`: shared candidate
  generation, direct LLM I/O and late-result guards, not a title session.

The **original design pass** ran no application build, provider request, crash
test or benchmark. It performed a walkthrough and source/contract review, with a
bounded read-only scout and naming brainstorm. The later temp-only executable
follow-up is separately scoped in §7. Storage, execution and browser evidence
remain in their respective reports. These establish bounded facts, not that the
production class design is implemented, performant or fully qualified.
