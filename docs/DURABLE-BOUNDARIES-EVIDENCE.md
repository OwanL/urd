# Durable intent and reference-lifetime evidence

**2026-11-04 — research, not a selected recovery policy or implementation.**

## Recommendation

Keep the selected shared SQLite store and managed attachment files. The missing
piece is **not another persistence engine**: it is knowing which request was
accepted, whether execution might have started, and which retained consumer still
needs each result or file.

- Correlate due-request admission and its durable input in one transaction where
  possible. This can remove a local duplicate-delivery window, **not** make tools
  exactly-once. Recorded results are recoverable evidence, not instructions to rerun.
- Preserve snooze's existing idle/pending-work safeguard. Do not interpret hidden
  tabs as closed targets or silently make snooze pause scheduled work.
- Retain needed result/attachment references independently of a worker's history.
  Conversely, a shared result is not permission to keep a closed worker's transcript.
- Prefer explicit, small recovery/cleanup mechanisms. No scheduler framework,
  replay engine, schema, APIs, class layout or independent-worker cascade is selected.

**No new executable probe was justified.** Existing tests establish bounded SQL
atomicity and conservative effect uncertainty; a new toy scheduler would mainly
prove a policy invented for the toy. This report supplies the missing failure and
reference ledger, plus precise follow-up cases. No earlier tests were rerun.

## Authority and evidence key

**I** = agreed Urd outcome; **W** = owner-reviewed working sketch / retained Pie
behavior, not a final Urd API; **C** = mechanism candidate, not approval;
**H** = consequential human recovery/attention trade-off still open.

- **A:** [Architecture](ARCHITECTURE.md), especially “Retain assignments and
  results”, “Practical crash recovery”, “Closed sessions”, “Keep session snoozing”,
  “Keep original chat history”, “Keep tool input/output inspection”, “Scheduled
  follow-ups”, “Image attachments” and “Session storage”. Full README/architecture
  and the three owning reports below were read, not just recent packaging work.
- **S:** [Session design](session-design.md) §§1, 3–4, 6–7. Its **42-assertion**
  fixture establishes guarded edits/close and one result surviving worker close/
  restart; it explicitly excludes notifications, follow-ups, attachments, bounded
  children, compaction and reference cleanup.
- **E:** [Execution design](execution-design.md), “Composed follow-up”. Its
  **47-assertion** fixture preserves a recorded result and blocks an unresolved
  call after a rolled-back result write. Whole-history test envelope, in-process
  reload; not the actual session owner or a process-kill qualification.
- **Q:** [SQLite validation](sqlite-validation.md), “Failure boundaries” and
  “Experimental-contract and integration gaps”. Bounded process interruption
  supports database transactions; attachment publication/cleanup, delivery evidence
  and compact final usage are absent. The reduced adapter is not the domain model.
- **P:** Pie contracts/source at `cc82abb0f317f31341d3a6f12aa1545f6be1fbea`,
  listed below. Source inspection verifies reference behavior, not live use or an
  obligation to inherit its sidecars, process layout or retention limits.

“Never dispatched” below means **positive recoverable evidence from an enforced
boundary**, not merely no result/start event in history. “Possibly dispatched”
includes a crash immediately after recording an intent-to-start but before the
actual call. That conservative false positive is real. Local input acceptance,
model consumption, tool outcome, explicit assignment finish and requester notice
are five different observations.

## 1. Durable intent, delivery and effects

All restart rows assume the backend has returned; nothing runs while it is off.
UI disconnection alone does not stop authorized backend work. “Open” describes
session lifetime, not selection/visibility; snooze is handled explicitly below.

| Failure boundary / evidence | Required outcome | Smallest candidate or open trade-off | Grounding / qualification |
| --- | --- | --- | --- |
| **Intent absent or write uncommitted; acknowledgement lost** | Do not claim rejection solely from a timeout or missing history. Known pre-admission rejection is distinct from unavailable evidence. | **C:** read correlated durable evidence before any retry. Absence proves non-admission only if every admission uses that authority and no old attempt can still commit. No generic retry-on-error. | A recovery; S §3; P assignment ambiguity. Not an executed Urd delivery test. |
| **Assignment saved; worker open; execution not established** | **I:** preserve requester, worker and work/outcome facts across restart. Saved assignment is not success or blanket replay permission. | **W/C:** retain accepted identity/revision; distinguish known unstarted input from interrupted execution. Ordinary-assignment automatic start/resume controls remain open; the automatic-due rule is not a rule for every unfinished assignment. | A assignments; S §§3–4. S tested interrupted admission, not all queued/unstarted recovery. |
| **Due follow-up saved, never dispatched; open target** | **I:** preserve deadline and run automatically when due, including overdue after restart, through normal admission. Busy does not mean closed or permission for a parallel run. | **C:** atomically correlate one registration with one accepted input; if admission is unavailable, retain the due request rather than consuming it. No manual-resume requirement for this safe case. Exact busy/queue ordering is not selected. | A scheduled follow-ups; S due flow. No Urd durable-timer test. |
| **Due input committed; crash before execution starts** | Do not generate a second input from the same registration. If enforced evidence proves execution never started, this is still the authorized due request, not replay of an uncertain effect. | **C:** commit input plus registration disposition together; recover that input, not another delivery. A start marker committed before external dispatch narrows the safe-unstarted set. It cannot prove the call actually happened. | S atomic claim/admission proposal; Q shared transactions. Composition untested. |
| **Model/tool dispatch possible; no durable tool result** | **I:** retain uncertainty; no blind replay, invented success or assumption that cancellation undid effects. Positive provider-terminal evidence is needed even before initial tool dispatch. | **C:** a narrow pre-invocation unresolved marker and matched result commit. Read-only reconciliation may establish an outcome; silence cannot. **H:** how the person deliberately proceeds when verification is impossible. | A recovery; E terminal guard and rolled-back result evidence. Process death need not stop an external command. |
| **Tool result committed, but no assignment finish** | Do not execute that historical call again. A saved tool outcome or idle worker does not establish completion of the assignment. | **W/C:** load the actual call/result pairing; preserve unfinished task status. Resuming model work is separate from repeating a tool or inventing the worker's finish. | E reload; S explicit-result flow. No all-route recovery guarantee. |
| **Explicit assignment result committed; notice/ack lost or requester snoozed/offscreen** | **I:** result remains available across restart and worker closure while another open requester needs it. **W:** identical finish returns retained evidence; stale/conflicting finish rejects; notice does not start an idle model turn. | **C:** inspect committed task fact regardless of notification receipt. A durable unread/result projection may avoid a separate delivery queue. Model-context notification replay/deduplication still needs qualification; “notice read” is not permission to delete the result. | S §3 and fixture; P information/finish rules. Durable notification recovery was not tested. |
| **Target closes before due admission; or races admission** | **I:** cancel/discard its follow-up; never reopen or redirect it. If admission already won, closure must stop/fence owned work; it cannot undo an effect. | **C:** compare target lifecycle and registration/input mutation at the same authority. On restart finish cleanup of closing targets rather than dispatching into them. Preserve separately needed result facts. | A closure/follow-ups; S close flow. Timer/close composition untested. |
| **Target snoozed, or snooze races pending work** | **I:** retain history/deadline; snooze is neither closure nor a job pause. Restore alone starts no AI work and steals no focus. Preserve idle-only/pending-work safeguards. | **C:** mutually checked snooze/registration/admission prevents normal coexistence initially. **H:** if coexistence is later allowed, hidden due work versus early background tab restoration needs design. A recovered conflict cannot silently become cancellation or indefinite manual-resume delay. | A snooze; P blocks snooze for deferred targets and blocks restoring snoozed timer targets. Urd interaction still open. |
| **Requester closes; independent worker remains, or closes without a result** | No fabricated success. Retain facts still serving open work; do not use requester/creator closure to infer worker cancellation or cascade. | **C:** separate each participant's lifecycle from task evidence. **H:** independent-worker survival/warning/cascade remains unchosen. Final disposition of an unfinished task when one participant closes must be explicit, not a foreign-key side effect. | A relationships; S §§1, 4, 6. Existing fixture closes a specifically addressed worker with an already recorded result only. |

**Important limit:** local durable-input deduplication does not deduplicate model
billing or external tools. For example, `unresolved marker → tool creates file →
result transaction fails` is indistinguishable from a pre-effect interruption
without independent evidence. E already demonstrates that class of uncertainty.
Repeating its counted-effect test would not choose a usable recovery interaction.
A tool-specific idempotency key/query can help where actually supported; that is
not a general harness guarantee.

## 2. Reference lifetime and cleanup

A “reference” means a real retained use, not every historical mention of an ID.
The tables propose no reference-count schema or universal artifact graph. Both
shared durable uses and temporarily active reads/publication must be accounted
for; a SQLite snapshot alone does not keep an attachment file alive.

| Record / transition | What must remain available | Release/failure boundary and smallest candidate | Grounding / qualification |
| --- | --- | --- | --- |
| **Original history after successful compaction** | **I:** readable original messages/tool inspection while open, including snoozed; necessary attachment bytes remain. Compacted model context is a different view. | **C:** replace only the model projection after success; failed compaction keeps usable context. Model-visible omission is not deletion eligibility. Native representation/reset mechanics belong to the context investigation. | A original history; S §§1–2. No integrated retention test. |
| **Explicit earlier-prompt edit replaces suffix** | **I:** kept prefix and independently needed assignment/results; external effects are not rolled back. Removed conversation is not a revisitable branch/archive. | **C:** fence/settle affected work, atomically replace suffix and its retained references, invalidate stale detail/context identities. Remove obsolete derived context too. Old replay paths must stay fenced without preserving deleted tool payloads as a hidden recovery archive. | A linear history; S edit fixture tests rows/revisions, not attachments/child detail. |
| **Bounded child finishes, including nested helpers** | **I:** full inspectable messages/tool detail until containing chat closes, unless its conversation was explicitly removed by edit. Final report alone is insufficient. | **C:** release live resources at finish, not retained detail; fetch lazily. Containing-chat close removes helper detail/references after owned work stops. Do not infer arbitrary independent-primary ownership from this rule. | A hover/child inspection; S §§2, 4. Bounded-child lifecycle untested. |
| **Independent worker closes; another open requester needs its result** | **I:** explicit result, necessary coordination identity and any genuinely referenced managed attachments, across restart. Not the worker's entire history. | **C:** keep result material independently reachable by its consumers. Worker close removes worker-owned history references only. No “cascade delete tasks by worker” or implicit full-history retention to keep one result link working. | A summary/closure; S result retention fixture, without attachments. |
| **Result inspected, copied or referenced by another open session; later consumers close/edit** | Needed facts/material follow their retained consumers, not whether a notice was delivered, a tab selected or the original worker alive. | **C:** explicit retained uses or owned copies; release only the removed use. A textual link to deleted worker history is insufficient. Final-use eligibility need not mean immediate physical deletion; no unconditional permanent task archive. | A shared facts/attachments; S retention sketch. Exact consumer-release mechanics unqualified. |
| **Managed attachment accepted normally** | **I:** managed bytes independent of original external file; stored identity/metadata resolves for UI, model requests and tools. Path text alone is not image delivery to a remote model. | **C:** publish complete bytes before acknowledging a ready reference; commit reference with its owning message/result. Stable identity should not be rebound to different bytes during cleanup/retry. A general large-tool-artifact policy is not selected. | A attachments; Q explicitly does not manage bytes. |
| **File created, reference row not committed** | No accepted ready message/reference yet; leftover bytes are not evidence of delivered input or a completed assignment. | **C:** prefer a recoverable orphan over a committed ready reference to absent bytes. Reclaim only after excluding active publication and retained references. Rollback must not blindly delete a shared/pre-existing file. Private staging or short per-item coordination are alternatives, not chosen layouts. | Reasoned two-store failure case; no new runtime claim. SQLite does not roll back ordinary file writes. |
| **Reference row retained, file missing/unreadable** | Keep truthful message/result identity and expose unavailable bytes; do not claim intact image/context, substitute another file or erase evidence to hide the fault. | **C:** check on resolution; distinguish access/I/O failure from confirmed absence. Explicit repair/re-attachment can restore bytes if valid; do not silently reread a mutable original path or rerun the producing tool. Row-first publication needs an explicit not-ready state, otherwise it creates this failure on ordinary interruption. | A managed-copy requirement; reasoned failure handling. No corruption/repair implementation tested. |
| **Last reference removed while another session acquires one or a reader/tool uses bytes** | Shared or in-use bytes must not be deleted by stale “zero references” knowledge. Closing a UI or backend exit is not proof an owned external command stopped. | **C:** coordinate reference acquisition/publication, final eligibility check and unlink; protect bounded active uses until settled. Merely scanning twice or using a delay does not close the race. Deletion failure leaves cleanup pending, not a reason to resurrect closed history. | A shared references; S Stop/close limits; Q file cleanup gap. No GC/lease framework selected. |
| **Containing session final close; restart midway** | **I:** no readable/resumable closed conversation or full tool archive; keep only compact usage plus coordination material still needed elsewhere. Snooze never takes this path. | **C:** fence work, settle owned uses, commit final logical deletion/needed-reference changes with compact totals where possible; subsequent cleanup is repeatable. Crash before commit must not claim completed close; after commit cannot restore history on a guess. | A close/summary; S/Q atomic row evidence only, not complete close+file+summary integration. |
| **Small final usage summary** | **I:** lean tokens/estimated cost/time/errors, not messages/tool bodies/attachments for analytics. Incomplete observations must not turn into apparent zeros or invoice certainty. | **C:** summarize from retained running totals before discarding detail; couple finalization to close so retry/restart neither loses nor doubles totals. Late observations must not recreate history. Exact fields/coverage markers and cleanup timing are routine design unless richer retention is proposed. | A small summaries and analytics limits; Q summary absent. No need to import Pie's detailed ledger. |

**Concrete cleanup counterexample:** cleanup observes zero references; session B
then commits a reference; cleanup deletes the file. Every SQL transaction can be
correct and B still loses its attachment. Similarly, a publisher can finish writing
bytes before its reference commit while an orphan scan deletes them. These are
interleaving arguments, **not newly reproduced defects in a Urd implementation**.
A shared exclusion boundary can prevent them; a long delay merely makes them rarer.
In-flight file readers also need a lifetime rule—database referential integrity
alone is insufficient. Choose that small mechanism when implementing the path,
not a general garbage collector now.

## Contradictions and choices exposed

1. **Acknowledged-record wording needs reconciliation, not a stronger crash
   requirement.** A “Session storage” paragraph allows loss of recent acknowledged
   work; the following guarantee says every acknowledged record replays identically.
   A “Practical crash recovery” allows reasonable recent progress loss and excludes
   a power-loss guarantee. Recommended clarification: distinguish provisional/live
   progress, returned committed database records, and machine/power loss. SQLite's
   [official synchronous documentation](https://www.sqlite.org/pragma.html#pragma_synchronous)
   explicitly distinguishes application-crash durability from WAL/NORMAL's possible
   rollback on system crash/power loss. This does not establish atomicity with
   attachment files, every failure mode, or a new fsync/loss-budget requirement.
   No owning document was changed in this investigation.
2. **“Open” cannot mean “has a tab”.** Pie's deferred registry checks tab membership
   and can restore closed targets; Urd explicitly forbids that restoration while
   preserving snoozed history. Copying either its target test or ordinary-close
   retention verbatim would contradict Urd. Retaining the existing snooze safeguard
   avoids choosing hidden execution or early restore by accident; qualifying the
   registration/snooze race is still necessary.
3. **Persist-before-notify does not yet mean reliable attention.** A saved result
   can survive while its notification is lost. Rebuilding a non-waking unread/result
   view is a smaller candidate than a general message delivery system. It still
   needs a clear way to find unfinished/uncertain work after restart. This is not
   permission for an automatic AI nudge, duplicate context insertion or popup storm.
4. **Final closure and shared results are compatible only with separate lifetimes.**
   Deleting all worker-owned rows loses a requester's evidence; keeping all worker
   history violates final closure. Retaining just the independently needed material
   resolves the tension. Likewise, a still-open compacted conversation—not just its
   current model window—is a retention consumer.

Logical deletion is not secure physical erasure, immediate database shrinkage or
proof that already delivered bytes disappeared from another client/process. SQLite
[auto-vacuum](https://www.sqlite.org/pragma.html#pragma_auto_vacuum) and
[secure-delete](https://www.sqlite.org/pragma.html#pragma_secure_delete) documentation
make clear that deletion/storage reclamation are distinct. No erase policy,
backup promise or closed-history retrieval feature is added here.

## Consequential morning questions — not field-name votes

**Bring one recovery scenario first:** “The command may have changed files, but
Urd died before recording its result. If verification is impossible, may you
explicitly move on with that action still marked unknown, or must this chat stay
stopped until you reconcile it?” Recommend an explicit move-on/abandon path with
uncertainty preserved, plus a distinct deliberate repeat action explaining the
risk—not silent replay or guessed success. Stronger blocking reduces accidental
continuation but can strand useful work indefinitely. Exact controls and attention
placement remain for the owner; other sessions need not be frozen.

**Conditional snooze question, only if relaxing the retained safeguard is wanted:**
“When a snoozed chat has an authorized check due, should it return in the background
without taking focus, or do the check while hidden?” Recommend retaining the
pending-work exclusion initially; neither alternative is approved by this report.
Pausing/discarding the check silently is not an acceptable third interpretation.
This is not a required re-vote on keeping snooze or automatic scheduled work.

**Existing independent-close decision stays deferred to concrete UI:** survival
versus warning/cascade for independent workers, especially human-steered ones.
No transaction/file probe can choose it. Ordinary reference accounting, SQL
transaction boundaries and compact-summary fields do not need owner votes.

## Smallest useful follow-up and limits

Before implementing recovery controls, walk through the uncertain-command scenario
above with the owner. Then qualify **one due-registration → durable input seam**
against the actual admission owner: deterministic interruption before/after the
shared commit and before/after the execution-start boundary; one closed-target
race and one snooze-registration race. Assert one accepted input for a registration,
automatic handling of proven-unstarted due work, no replay of ambiguous effects,
and no reopened closed target. It needs a fake effect and real transactions, not
a scheduler or a complete session prototype. This is a recommendation for later
qualification, not overnight implementation authority.

When managed attachments enter implementation, the smallest separate acceptance
case is two open consumers sharing one synthetic file: edit/close one, interrupt
publication/cleanup at the two-store boundaries, then resolve from the survivor.
Force zero-reference-check versus new-reference interleaving and missing-file
resolution. Couple compact-summary finalization to the actual close path before
retaining real data. Do not claim the existing session/execution/SQLite checks
already cover these compositions.

**Verified here:** requirement/report review, bounded read-only Pie source/contract
inspection, official SQLite documentation, and independent method/scope and final
acceptance reviews. Neither review ran tests; final review found no consequential
error or authority overreach in these bounded claims.
**Reasoned, not executed:** the two ledgers, candidate mechanisms and cleanup
interleavings. No build, restore, dependency install, live account/provider,
private data, fixture process, scheduler/GC implementation or existing-test rerun.
No `urd:heavy-local-work` lease was needed. Only this new report is a repository
deliverable; notes/review evidence are under
`%TEMP%/urd-foundation-streams-6268b517/durable-boundaries/`.

### Exact reference anchors

Urd baseline inspected: `08d869183a351c2dfe5ebd649eb8d22b0b2c4654`.
Pie paths below are in `C:/dev/repos/pie`, at the reference commit above:

- `docs/contracts/AGENT-SESSION-CONTROL.md`: Assignments and results, Information
  and waiting, Retention and uncertain operations. `session-tasks.ts:250–320`
  under `harness/agent-processes/coordinator/` confirms immutable finish and
  retirement behavior. Pie's registry is coordinator-lifetime, not Urd durability.
- `docs/contracts/DEFERRED-TRIGGERS.md` §§2–4 and `STATE_CONTRACT.md` “Deferred
  Trigger Delivery”: dead-owner/pre-dispatch versus acknowledgement ambiguity.
  `application/backend/deferred-triggers/registry.ts:291–448` and
  `store.ts:255–316` show claim/start/ack and restricted recovery. These paths
  were read, not copied or run; Urd does not inherit cross-host claim artifacts.
- `docs/contracts/STATE_CONTRACT.md`: Session Snooze, Session Cleanup, Snapshot
  Recovery, Execution Ordering, Queued Follow-ups. In
  `application/backend/session-actions/service.ts:425–445`, timer restoration
  rejects snoozed/closing targets. Pie's ordinary closed-history reopen is
  deliberately not Urd behavior; its broad accounting is also not selected.
