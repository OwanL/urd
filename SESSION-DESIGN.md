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
  operations sharing model-call behaviour, rather than full session lifecycles.
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
- **File/project layout deliberately deferred.** After reviewing the derived
  example in §5, the owner said it was too early to choose. Neither one backend
  project nor a Core/Server split is approved. This report must not be used as
  permission to generate that layout.

## 1. Three kinds of ownership

| Owner | Facts or resources | Lifetime |
| --- | --- | --- |
| **Durable records** | Session identity/kind/settings; ordered original history; model-context representation; assignments/results; explicit delayed follow-ups; usage totals; attachment references | SQLite/files under retention rules; survive backend restart. Not serialized live object graphs. |
| **Live session/execution objects** | Admission/mutation guard; current execution identity; model/tool work, cancellation, queued input and waits; bounded live stream state | Backend-owned. A session handle can be loaded lazily; an execution exists only while work is active. Neither needs its own thread/process. |
| **Client presentation** | Selected session, unsent drafts, finite page/detail caches, view/connection state | Client-owned where the approved simplicity/cost conditions permit. Never authority for whether an agent is running or an action occurred. |

A stable session ID is allocated independently of its mutable title. Browsing
history does not start an execution or require loading all history. The database
is durable truth; an in-memory session is a controlled handle/cache, not a second
independently writable history. Original readable history and the model's compacted
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
- `SessionExecution`: the current async job, including model/tool cycle, pending
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

Auxiliary cancellation belongs to its caller: a title's naming attempt/session,
or the context operation invoking compaction/pruning. Context-changing work
participates in session admission/busy state; a concurrent title only publishes
through a guarded metadata update. No auxiliary-session/job framework is needed.

Only the two session kinds form an inheritance tree. `SessionExecution`, tool
execution and auxiliary operations are **used/owned**, not inherited. No
`UserAgent`, `DelegatedAgent`, model-specific or role-specific session subclasses
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

## 3. Durable facts and boundary rules

Logical facts below are **not a final schema or new class per row**:

| Fact | Required distinction |
| --- | --- |
| Session/history | Stable identity and kind; open/snoozed/closing status as needed; ordered message/detail identities; suffix-edit identity/revision; original history versus model context. Missing/closed targets reject mutations—append must never implicitly recreate them. |
| Admitted input/execution | Input identity and queued/delivered/interrupted status; correlation to the execution allowed to report progress. Transport receipt, model consumption and effect completion are not interchangeable. |
| Assignment/result | Task ID, requester, worker, accepted revision, state, explicit outcome/result. One nonterminal assignment per worker. Identical finish returns retained evidence; stale/conflicting finish cannot overwrite it. Persist before notification; notifications do not wake an idle requester by default. |
| Follow-up | Stable registration/target, intended due time, request and dispatch/uncertainty evidence. A never-dispatched due request is not an interrupted effect being replayed. |
| Retention | Compact usage after close; coordination facts while open participants/retained references need them; attachment references across sessions. No permanent conversation archive or unconditional infinite assignment retention. |

Use a narrow `SessionStore` with intent-specific transactions, not an ORM,
event-sourcing engine or interchangeable-store framework. A small session lookup/
creation coordinator (working name `SessionManager`) owns one active handle per
ID and cross-session admission; it does not absorb the model loop. Session
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
**tool-invocation wrapper** is unrelated to the rejected extra model-client facade.
Provider-specific terminal success must be established before tool dispatch:
complete-looking arguments alone can survive a failed/incomplete/truncated response.

## 4. Walkthrough: where the sketch must hold

These are proposed mechanics checked against recorded requirements, not claims
of working Urd behaviour.

| Flow | Ownership and consequential boundary |
| --- | --- |
| **Submit / stream** | API routes a command to the primary; admission saves the accepted input, then starts one execution. Provider/tool events carry its identity through guarded updates. Bounded live deltas may precede batch persistence, but provisional output is not called durable. A title candidate cannot block stable identity or overwrite a newer manual title. |
| **Input while busy** | Keep steering at a safe model/tool boundary and preserve preflight follow-up semantics so a second prompt does not silently replace the first request's own response. Notes do not start idle work. Ordinary input, notes and assignments remain distinguishable; new assignments cannot merge into a busy worker. |
| **Edit pending input** | Compare the exact pending input/delivery identity before replacement. Reject a delivery race; preserve whether input is steering or follow-up. Do not clear unrelated notes/results as a shortcut. No transcript suffix mutation or tool replay is involved. |
| **Edit earlier prompt** | Stop and await affected execution; block new admission; atomically replace the suffix and invalidate stale execution/detail/context identities. Removed conversation does not roll back files/network actions or erase separately needed assignment evidence. Failure after committed replacement cannot restore the old tail on a guess. Then start replacement input deliberately. |
| **Cancel / Stop** | Request cancellation of active/queued work, tools, bounded children and applicable auxiliary work; settle only when owned work has actually stopped. Keep “stopping” if unconfirmed; do not admit an overlapping replacement. A token/disposed object is not proof an external process died. Independent primary workers are not implicitly cancelled through a creator token. |
| **Bounded child** | Create a `SubagentSession` with stable caller identity and bucket selection; share the execution code, not its model context. Return one direct outcome. Release live execution at finish; retain lazy-readable child messages/tool detail with the containing chat. |
| **Independent worker / human steering** | Create another `PrimarySession`, then admit explicit durable work from its requester. Human input uses that same session; it neither changes type nor cancels the creator's cleanup authority. Idle without an explicit finish is awaiting-result, not success. Preserve default running-worker cleanup skip and instructions to inspect human use/results. |
| **Parent final close** | Fence admission, stop bounded owned work, then delete the containing conversation/helper detail and cancel follow-ups targeting it. Retain needed assignment/results independently. Independent-worker cascade/survival is an owner gate: this sketch must not implement either implicitly. Self-close must not wait for its own execution to die before returning acceptance; cleanup completion is separate. |
| **Browser disconnect** | Dispose that client's subscriptions, not the session or execution. Reconnect reads bounded durable/live state and command evidence; never blindly resubmits a command with a lost acknowledgement. Unsent drafts are not accepted work. |
| **Backend restart** | Reconstruct saved sessions and assignments/results; no live execution objects survive. Mark interrupted/uncertain work honestly. Recover completed results without invoking tools again; missing results do not prove effects never happened. An unresolved effect needs deliberate resolution, not a fake success or transcript-repair rerun. |
| **Due follow-up** | A never-dispatched saved request for an open target runs automatically when due/after restart, through ordinary admission, not a parallel execution. Claim/admission must be correlated atomically so one registration cannot become two inputs. Previously dispatched/ambiguous work is not automatically redelivered. Closed targets are discarded, never reopened. |
| **Snooze / restore** | Idle-only admission, no pending work bypass. Preserve history and deadline; restore visibility without focus or an AI turn. Keep existing pending-work/follow-up safeguards until their interaction is explicitly designed; snooze is not a hidden execution pause. |

A small durable dispatch marker/transaction can conservatively leave ambiguous
work for resolution. This is not a promise of exactly-once external effects,
a workflow-replay engine, per-token fsync, or a specified loss-time budget.

## 5. File/project proposal — reviewed, deliberately NOT selected

The following example shows how responsibilities *could* map to files. The owner
considered choosing the overall split too early; derive the real structure from
validated flows, and bring it back before scaffolding production code.

```text
src/Urd.Backend/                 illustrative single C# project
  Sessions/                     SessionBase, PrimarySession, SubagentSession,
                                SessionExecution, SessionManager, TitleGenerator
  Context/                      HistoryCompactor
  Capabilities/                 CatalogPruner
  Models/                       IChatClient adapters/configuration
  Tools/                        ToolExecutor and actual tools
  Storage/                      SessionStore and managed attachments
  Api/                          commands, bounded reads, subscriptions
src/Urd.Web/                    illustrative UI project location
tests/Urd.Backend.Tests/        owning behaviour/flow tests
```

`Api → session coordinator/objects → execution → IChatClient/ToolExecutor`;
session mutations use `SessionStore`. Execution reports through a guarded callback
rather than receiving unrestricted history writes. Read-only API projections may
query the store without creating an execution. Storage and provider clients do
not depend on HTTP or the frontend; only the composition root wires them together.

**Alternatives:** one backend project with explicit folders minimizes initial
wiring; `Urd.Core` + `Urd.Server` enforces HTTP separation at compile time but adds
assembly/public-interface boundaries. Neither implies a process per session.
No empty folder/class generation, generic repositories, mandatory event bus,
base-class hook framework or duplicated frontend session hierarchy is justified.
Browser Design's owner-approved TypeScript/Preact direction settles neither
backend project boundaries nor frontend classes/state ownership/folders; those
remain provisional, not a mirrored backend class hierarchy.

## 6. Acceptance sequence and remaining gates

**First vertical-slice acceptance:** create a primary with stable identity;
submit/stream through a deterministic `IChatClient`; save/reopen a bounded recent
window; switch another session while streaming; disconnect/reconnect without
cancelling or duplicating work; Stop settles before another execution starts.
Then exercise pending/earlier edits with delayed stale callbacks. Use a real
qualified provider separately; a fake client proves control flow, not entitlement,
provider terminal semantics or native-context fidelity.

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
- Lossless provider/model-context representation and effect uncertainty: qualify
  early with the chosen route; neither a common interface nor retries solve it.
- How much behaviour truly belongs in `SessionBase`: test both kinds before
  expanding the base. Keep the working hierarchy small, not “set in stone” APIs.
- Overall module/project/filesystem structure: owner review remains open. A
  working class sketch is not approval of a production solution layout.

Routine method names, lock primitives, batching intervals and file subdivision
can follow evidence; they need not become a design questionnaire. The smallest
next step is an owner-aligned, disposable flow test of the reviewed session split
using a fake model and the SQLite facts above, then derive the production project
layout. This assignment does not authorize that production scaffold.

## Evidence and limits

Read Urd's README/ARCHITECTURE and relevant model/discovery/storage research;
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
  generation, direct model I/O and late-result guards, not a title session.

This stream ran **no application build, provider request, crash test or benchmark**.
It performed a design walkthrough and source/contract review, with a bounded
read-only scout for interaction evidence and a separate naming brainstorm.
Executable storage evidence is in `SQLITE-VALIDATION.md`; execution/browser
experiments belong to their respective reports. Those establish bounded facts,
not that this class design is implemented, performant or fully qualified.
