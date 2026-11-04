# Shared SQLite: first-slice validation

**2026-11-04 — conditional go.** Keep the selected shared SQLite/message-row
layout. Transactions passed the bounded recovery checks below; no evidence here
justifies another engine comparison. **Do not promote the experimental adapter
or its contract unchanged.** Fix its preview defects, give production mutations
explicit lifecycle/revision rules, and add schema-version handling first.

This is validation evidence, not approval of application classes, file layout,
parent/child closure policy or a production schema. No new owner decisions were
requested. Session Design and Browser Design received the relevant constraints.

## Scope and reproducibility

Windows x64, .NET SDK **10.0.403**, runtime **10.0.14**,
`Microsoft.Data.Sqlite` **10.0.14**; existing repository toolchain, no installs.
Tested source: `experiments/transcript-harness/`, at repository revision
`2d3fa93b06e101643d986d26000060c0b61922d5`. Shared/adapter source was not edited.
Configuration: **WAL, synchronous=NORMAL**, default auto-checkpoint; adapter calls
return after transaction commit. This is process-crash evidence, not power-loss
qualification or a new exact loss budget.

All disposable scripts, fixtures and logs are under:

```text
C:\Users\OwanLazic\AppData\Local\Temp\urd-foundation-streams-6268b517\sqlite-validation
```

Call this directory `$V`. Executable reproductions (PowerShell):

```powershell
& "$V\baseline.ps1"
& "$V\stage1.ps1"
& "$V\probes.ps1" -RunName probes-run-3
```

Use **fresh fixture directories**: baseline/stage1 paths must not contain an
old run; probe run names must be new. Scripts contain exact build/executable
arguments and write `commands.log`; no source modification is needed. Baseline
builds the existing harness solution, sqlite-rows adapter and its focused tests
in Release, with `--no-restore -m:1 -nr:false -p:UseSharedCompilation=false`.
The temp probe references those rebuilt DLLs; its project has no new packages.
Baseline redirects `TEMP`/`TMP` into `$V\temp`, isolating tests which otherwise
use fixed temp directory names. Runs have 120-second process-tree timeouts.

Acquire/start **exclusive `urd:heavy-local-work` and check memory** before
reproduction. Our two leases were released after owned test processes exited.
Stage1 ran with approximately **1.83 GB free**, before headroom fell to around
0.6 GB; it must not be repeated/scaled under that later headroom. No 1 GiB tier,
soak, browser, live tools, providers or user data were exercised.

## What actually ran

| Check | Result and evidence under `$V` |
| --- | --- |
| Shared selftests | **23 passed**; `selftest.stdout.log`. Reference/memory-only kill checks establish harness mechanics, not persistence. |
| SQLite-focused correctness | **9 passed**; `focused.stdout.log`: reads, previews, streaming batches, suffix edits, deletion/assignment policies, reopen and Unicode round trips. |
| Shared SQLite smoke | **634 operations, 10 checkpoints**, full-content oracle success; `smoke/report.json`, `smoke/trace.jsonl`. |
| Clean and abrupt reopen | Both passed at OsFlush tier; `validate/validate.json`. Twelve acknowledged messages plus a settled assignment survived. The extra unawaited append was present after kill. |
| Stage1 mixed fixture | **760 operations, 14 checkpoints**; `stage1/report.json`, `stage1/trace.jsonl`. Built content: **20,971,724 + 1,048,927 UTF-8 bytes**, plus small sessions. Eight read/append/tail-edit cycles, results capped at 256 KiB. |
| Temporary concurrency/failure checks | Two fresh runs: `probes-run-1/results.json` and `probes-run-2/results.json`; source `probe/Program.cs`, build/run script `probes.ps1`. Selected transaction checks passed; preview defects were reproduced, not hidden by a green summary. |

### Concurrent activity — three different claims

- The shared runner is **serial**, even with many session IDs. Its smoke and
  Stage1 results are not concurrency or live-agent capacity evidence.
- Temporary probe: **one writer connection**, two independently owned reader
  connections on dedicated threads, eight session identities, 40 rounds.
  It committed **328 final messages and 320 draft batches** per run while
  readers fetched recent content/previews. Full final content and dense order
  matched independent expectations. Reader iterations were **636 / 776**;
  preview cursor inconsistencies were **19 / 17** (defect below).
- Separate probe: **two concurrent writer connections** appended to the same
  session, 100 records each. Both runs returned 200 unique dense ordinals and
  exact final content. SQLite still serializes the write transactions; this
  does not establish parallel database writes, fairness, sustained contention,
  multiple backend processes, or 50/210 live agents. No connection was shared
  concurrently between threads.

### Failure boundaries — stronger than a timed kill, still bounded

Each run used four baseline messages, an unrelated session, open and settled
assignments, and a persisted draft. The parent verified full messages/previews,
all assignment fields, draft text and deletion tables; reopened stores passed
`PRAGMA integrity_check` and accepted a subsequent append.

| Child interruption | Observed reopened state, in both runs |
| --- | --- |
| Suffix replace: after deletes, after one replacement insert, after all inserts but before commit | Entire original history and metadata; no partial replacement. |
| Suffix replace: after successful API return | Entire replacement history and metadata. |
| Assignment settlement: after UPDATE but before commit / after return | Original open assignment / exact settled result. |
| CancelOpen deletion: after row cleanup but before commit / after return | Entire original session with open assignment / no session, messages, preview rows or draft; open assignment cancelled, earlier settled result retained. |
| Draft batch: after successful API return | Both acknowledged draft batches retained, without becoming visible messages. |

Suffix pauses use a test `IReadOnlyList` enumerator reached by the **real**
`ReplaceSuffix` after its deletes/inserts. Settlement/deletion pauses use
connection-local TEMP triggers invoking a test callback, installed through
reflection on the adapter's connection. No production code/schema was changed.
The child signals its reached boundary; the parent checks a separate reader
cannot see partial state, disposes that observer and clears its connection
pools **before killing** the child, then opens a new physical connection.

These are **injected mid-operation process kills at known boundaries**, not
kills during SQLite's COMMIT implementation, torn-device-write tests or
power-loss tests. The shared `validate` instead sends a raw append, waits
120 ms and kills; its unawaited request may already have completed. Do not call
that a demonstrated mid-write interruption. No external side effects were
executed or replayed. Delete here is the reduced harness operation, **not**
approval of cascaded closure of production workers.

## Supported adapter defects — scoped follow-up, not engine failures

Paths below are in
`experiments/transcript-harness/adapters/sqlite-rows/Urd.TranscriptHarness.Adapter.SqliteRows/SqliteRowsTranscriptStore.cs`.
Both probe runs reproduced all three findings. Their `status: passed` means
the observation/check completed; inspect the explicit mismatch fields.

1. **Concurrent preview cursor is inconsistent with its own items**
   (`OpenRecentPreview`, lines 156–179). `COUNT(*)` determines `FromOrdinal`
   before a separate latest-window SELECT. An append between them produces,
   for example, `FromOrdinal=2` with first item ordinal `3`. Minimal repro:
   two adapter instances on one DB; repeatedly append on one while calling
   `OpenRecentPreview(session, 8, 24)` on the other, asserting the first item's
   ordinal equals `FromOrdinal`. The probe recorded 19/636 and 17/776 failures.
   **Fix before using independent readers:** return window/cursor from one
   consistent query/snapshot; audit other multi-query reads and >256 fallback
   under edits. The current serial host does not expose this race, and sharing
   one adapter instance across threads is not a proposed fix.
2. **An acknowledged zero-part message disappears from preview** (line 172,
   metadata-led inner join). `AppendMessage("s", new("assistant", []))`;
   `OpenRecent("s",1)` returns one message, but `OpenRecentPreview("s",1,24)`
   returns none. The contract/append API does not reject this shape. Decide
   whether it is valid: preserve the message header in previews, or reject it
   before acknowledgement with consistent contract/tests. Never silently omit
   a stored record from one view.
3. **A valid emoji crossing the stored-prefix boundary corrupts the preview**
   (`InsertMessage`, line 540). Append `new string('a',255) + "😀z"`, then request
   a 256-character preview. Stored UTF-16 slicing cuts the surrogate pair;
   the returned last code unit is **U+FFFD**, not the expected **U+D83D** under
   the current exact-prefix contract. Full message content remains exact.
   This is valid input, not the README's already-invalid lone-surrogate case.
   Define a Unicode-safe bounded-prefix rule and align writer, oracle and
   transport tests; rejecting ordinary emoji is not a reasonable workaround.

## Read-path/resource observations, not UI qualification

One Stage1 run, uncontrolled warm filesystem cache and background load; no
performance thresholds or comparative ranking are inferred:

- Workload preview RPC: **21 samples**, median **0.714 ms**, max **1.647 ms**.
  Host preview allocation: **211 calls including verification**, max
  **40,584 bytes**. These are not click-to-visible browser timings.
- Full `openRecent` RPC: 27 mixed-size samples, max **456 ms**; full `pageOlder`:
  eight samples, median **190 ms**. Returning a count-bounded window is **not a
  payload-byte bound**. Do not use full-result reads to populate tab previews.
- Store-host replay peak: **738 MiB private committed / 653 MiB working set**,
  including interleaved verification requests. Cumulative managed allocation
  was approximately **14.9 GB**, not retained memory. A full range call allocated
  up to **71.7 MB**. These figures include the harness's full-content/JSON path,
  not just SQLite's cache; they do not establish a leak or engine pathology.
- Reported post-run DB/WAL/SHM footprint: **27,808,832 bytes, three files**.
  This is not a WAL-growth, vacuum or long-lived-reader retention test.

The default preview path avoids decoding payload JSON only while requests fit
its stored prefix. **Above 256, fallback decodes the entire message envelope**,
not merely one part as some README wording implies. `FetchToolResult` also
decodes that message's envelope. Profile bounded UI reads and realistic context
range assembly before scaling; this evidence does not justify switching to
content blocks yet.

## Experimental-contract and integration gaps

These are **not settled production semantics**, and were sent to Session Design:

- `SessionMeta` is only ID/count: no ownership, open/closing/closed state,
  admission revision, snooze, title, history identity or context projection.
  Append implicitly recreates a deleted ID (probe confirmed); late callbacks
  therefore need lifecycle/revision fencing above or within production writes.
- Assignments contain ID, target, title, Open/Settled/Cancelled and result text;
  no requester/worker identity distinction, accepted revision, outcome,
  consumer references or result-delivery evidence. Repeated settlement replaces
  a result, and creation accepts a missing target (both confirmed). The oracle
  rejects missing targets on its generated path; the adapter API itself does not.
  Terminal records survive target deletion indefinitely: useful evidence of
  retention, not a policy for retaining results only while live consumers need them.
- No durable follow-ups, dispatch/uncertain-effect facts, usage-after-close
  summary, model-context/compaction state or transactional multi-operation
  admission API. Remembering a draft/result does not authorize tool replay.
  Draft text persistence was checked directly because the contract has no draft
  read/recovery API; finalization accepts supplied content rather than deriving it.
- No schema version/migration or newer-schema rejection. `CREATE TABLE IF NOT
  EXISTS` is not evolution. Establish versioned, transactional upgrades before
  retaining real Urd data; no Pie migration is required. Foreign keys are off;
  application SQL currently owns consistency. Single-writer ownership, bounded
  reader snapshots and transaction boundaries need explicit production treatment.
- Attachment bytes are harness-owned files, not managed by this adapter.
  Shared reference retention, interrupted publication and file cleanup after
  final closure are **untested**. Logical row deletion is not secure physical
  erasure, immediate file shrinkage, or attachment garbage collection.

### Documentation/oracle reconciliation

The adapter README's first two reported inconsistencies are **stale**, not
current SQLite bugs: the executor now enforces strict `ReadRange` bounds and
clamps `PageOlder`'s end **before** subtraction. Shared selftests pass those
cases. The oracle's common `WindowOf` remains a clamping helper, not the range
policy. Cross-session active stream-ID reuse still diverges in source:
reference-store draft lookup can append to another session's draft; SQLite
rejects it. Current generated traces avoid the collision.

`Oracle.VerifyAll` checks assignment status/result, **not target/title identity
or unexpected extra assignment records**. It also cannot read draft bytes.
Our temporary checks compared every known assignment field and draft text
separately. Shared smoke success must not be presented as comprehensive durable
coordination verification. These are targeted harness follow-ups, not reasons
to replace the whole harness.

## Recommendation and next step

**Conditional go for the first integration slice**, using shared SQLite/WAL
with a narrow owner, not the experimental interface as Urd's domain model.
Smallest follow-up: a scoped preview/query correctness patch with regressions
for the three reproductions, plus a minimal versioned schema and explicit
session/revision/assignment rules agreed with Session Design. Then test one
real admit → append/stream → result → close/restart flow, including stale writes
and a result needed by another open session. Keep full payloads out of initial
UI windows. Do not begin another engine tournament or unapproved app scaffold.

Independent read-only reviews checked stale-document claims and the temporary
failure/concurrency method. A fixture/oracle mismatch was corrected **before**
execution; the corrected method was approved. Further limits: no soak, sustained
writer contention, disk-full/I/O-error injection, kill during COMMIT, power loss,
macOS/Linux execution, file-backed attachment lifecycle or browser performance
qualification. The artifacts are disposable local evidence, not permanent
release acceptance assets.

## Preview fix follow-up — 2026-11-04

The three preview defects above are fixed in the **experimental sqlite-rows
adapter**, not promoted into a production store:

- The message-led LEFT JOIN retains zero-part headers; the cursor comes from
  the actual window. A deferred read transaction holds one snapshot through
  metadata and >256 fallback reads, including after the metadata reader ends.
- Prefixes are now the **longest leading prefix** within the requested UTF-16
  code-unit cap without splitting a valid surrogate pair. Length/cap units did
  not change; this is not grapheme truncation. Shared contract helper, reference
  and oracle agree, with independent expected-string tests and real shared JSON
  codec round trips. Other adapter implementations were not changed/qualified.
- Requests ≤256 remain payload-free even when the safe stored prefix is only
  255. Larger requests can still decode an entire flagged message envelope.

Executable evidence is under the owned OS-temp sibling `sqlite-fixes-98a6`
(`$F`) beside `$V` above. Same .NET/SQLite toolchain; serial single-worker Release
builds, no new package dependencies. Commands and output are in each run folder:

| Run | Result |
| --- | --- |
| `$F/baseline.ps1`, unmodified source | 9 focused tests, 23 shared selftests, 634-op/10-checkpoint SQLite smoke, clean/abrupt reopen passed. |
| `$F/regressions-before/baseline.ps1`, new tests before implementation | Existing 9 passed; all 5 new preview test groups failed on the unchanged adapter. |
| `$F/after/baseline.ps1`, fixed source | All 14 focused tests and 23 shared selftests passed; 634-op/10-checkpoint SQLite smoke and clean/abrupt reopen passed. |
| `$F/concurrency/probes.ps1 -RunName after-1` | Original concurrent-read workload only: 328 messages, 320 draft batches, two readers, 697 reader iterations, **zero cursor mismatches**, exact final content and integrity check passed. |

As before, use fresh run/fixture directories, and acquire/start
`urd:heavy-local-work` before builds/tests. Low free RAM (~0.69–0.84 GB at these
runs) kept this to small fixtures; no Stage1 replay or benchmark sweep. The
permanent regressions use reader-local TEMP views/functions to commit a real
second WAL connection during reads: append/truncate, replacement and deletion.
They are deterministic (no sleeps), do not alter the persistent schema or add
production hooks, and assert the writer committed before checking the snapshot.
Unicode tests cover stored/caller/fallback caps, all four part kinds, reopen and
transport. A payload-column observation function verifies metadata laziness.
Independent read-only acceptance review checked the source, regressions and
recorded runs, found no supported blocking issue, and approved this scoped fix;
the reviewer did not independently execute tests.

**Remaining limits:** old already-corrupted stored prefixes are not repaired;
use fresh experimental fixtures. No schema migration or lifecycle rules were
added. The audit found separate `PageOlder`/`ReadRange` existence/count/window
snapshot races under concurrent edits; these remain outside this preview-only
patch and were reported to the coordinator. The shared host smoke is serial;
the rerun is not contention/soak/fairness evidence. Abrupt reopen still does not
prove a kill during COMMIT or power-loss safety. No production-readiness claim,
new owner decision or wider adapter qualification follows from these fixes.
