# Transcript storage experiment — plan and harness contract

## Status

**Agreed experiment programme, not a storage decision.** The owner approved
broad transcript-storage experimentation on 2026-11-04 after reviewing
[STORAGE-RESEARCH.md](STORAGE-RESEARCH.md); SQLite was considered promising but
remaining deliberately **unselected**. This document owns the contract for that
experimentation: candidate families, shared harness, correctness oracle,
measurement rules and staged advancement. It records design sketches required
before any implementation. It does not commit Urd to a storage technology,
schema or durability level.

Delegated work for all experiment stages uses **Ollama for every role,
including review; there is no Codex or other fallback**. This is a standing
execution control for the programme.

**Amended 2026-11-04** after the [binding review](STORAGE-BINDING-REVIEW.md):
family sketches were corrected against source/binding evidence, the oracle and
kill semantics were tightened, and setup-gate wording was simplified. SQLite,
LMDB and RocksDB probes passed Windows basic operations; LiteDB remains
provisional (no probe); Tsavorite standalone is not qualified.

## Purpose and boundaries

- Compare plausible transcript-storage designs on Urd's actual operations under
  one shared harness, then converge on a shortlist. The shared runner is the
  authority; families must not quietly diverge in workload or measurement.
- Deliverable of the programme: recorded evidence in a results document,
  following the conventions of [EXPERIMENT-RESULTS.md](EXPERIMENT-RESULTS.md).
  Not deliverables: production schemas, adapter code shipped as application
  code, or premature selection.
- Out of scope: UI/transport/rendering, model loop and providers, compaction
  logic, credential handling, power-loss durability, macOS/Linux execution
  (Windows first; later OS runs stay unexecuted until machines and approval
  exist), and replacement of the earlier microbenchmarks.
- **Binding viability is now established by the [binding
  review](STORAGE-BINDING-REVIEW.md):** `Microsoft.Data.Sqlite`, `LightningDB`
  (LMDB) and `RocksDB` probes all passed Windows basic operations on .NET 10,
  so those families proceed; LiteDB is fully managed but stayed unprobed and is
  provisional; Tsavorite has no official standalone package. That review's
  `Defer` verdicts about which engine should *lead* are its own prioritization,
  **not unviability evidence for working candidates**; harness-viability
  decisions belong to this programme's stages.

## Candidate families (hypotheses)

The families below are hypotheses to exercise, **not commitments**. A family
that Stage 0 fails is recorded as **prototype failed / not qualified for this
programme** with evidence and stops there; failed prototypes are not proof that
the underlying technology is unviable, and dominated families may also be
dropped (see advancement rules). Nine families enter the stages — the
native-binding ones all probed successfully and LiteDB is provisional, pending
its first Stage 0 smoke; Tsavorite is listed as **deferred, not rejected**. The
families deliberately span shared databases,
per-session databases, binary logs, files and hybrids; the owner wants broad
exploration before convergence.

**Encoding/compression are controlled within-family variants** (MessagePack,
Protobuf, MemoryPack; compression on/off). They are not additional families and
are not used to inflate the comparison. Variants are fixed per adapter, declared
in its report, and changed only when a family shows a measurement-relevant
reason.

| # | Family | One-line layout |
| --- | --- | --- |
| 1 | Shared SQLite, message rows | One database for all sessions; one row per message, indexed `(session, ordinal)`. |
| 2 | Shared SQLite, content blocks | One database; messages split into content-block rows so large text has separately addressable storage. |
| 3 | LiteDB documents | One LiteDB file; message documents with large content in separately retrievable documents. |
| 4 | LMDB indexed records | Single LMDB environment; ordered composite keys `(session, ordinal)` — the ordered keyspace is the index. |
| 5 | RocksDB indexed records | One RocksDB column family per purpose; keys encode `(session, ordinal)`. |
| 6 | Segmented binary log with index | Append-only per-session segments (fixed record budget) plus one explicitly specified external index. |
| 7 | SQLite metadata/index + external payloads | SQLite for metadata, indexes and edits; per-session payload files for large content, offsets recorded in SQLite. |
| 8 | Per-session SQLite + shared coordination DB | One small SQLite file per session, one shared database for coordination/assignment records. |
| 9 | Tsavorite (feasibility-gated) | Hybrid log with epoch-protection checkpoints; proceeds only if feasibility is established. |
| 10 | Bounded-read JSONL (reference) | One JSONL file per session with backward suffix reads; a reference baseline, not a presumed finalist. |

### Design sketches required before implementation

Each adapter must state, in an owned summary record, its layout, index, edit
mechanics, acknowledgement boundary and recovery rules before Stage 0 begins.
The sketches below start them; adapters refine, they do not redesign silently.

- **1. Shared SQLite message rows.** Classic row-per-message; large tool results
  stored in the row or in a spill table are an internal choice, declared. Edit =
  `DELETE WHERE session=? AND ordinal>=?` inside the append transaction.
  Recovery = SQLite WAL/rollback on reopen. Atomicity is genuine but WAL growth
  and whole-file footprint on many-huge-session datasets are tracked.
- **2. Shared SQLite, content blocks.** Message rows carry headers and part
  references; text blocks live in a blocks table. Aimed at bounded reads of
  large tool results and smaller message-row churn. Edit deletes message rows
  and reclaims blocks (orphan-cleanup rule must be explicit). Same WAL atomicity
  as family 1, plus block-reference invariants the adapter must document.
- **3. LiteDB documents.** Message documents indexed on `(session, ordinal)`;
  oversized content stored as separate documents retrieved by ID. Edit rewrites
  the suffix document set. Recovery relies on LiteDB's journaling — its actual
  crash behaviour is measured, not assumed. **Provisional:** no basic-operation
  probe has run for it, so its Stage 0 entry includes the first live smoke on
  the stable 5.0.x line.
- **4. LMDB.** Records ordered by composite byte keys `(session, ordinal)`; the
  ordered keyspace **is** the index — no separate bitmap/B-tree structure and no
  live-top pointer machinery is introduced. Suffix edit = one write transaction
  covering cursor-deletion of the replaced range plus the reinsertions,
  atomically committed, so no stale old-suffix keys are ever exposed through
  key reuse. **Write-multiply and mmap-reservation behaviour are measured** (see
  resource rules). Recovery = LMDB's single-writer transactional semantics on
  reopen; commit fsyncs by default and no weakening flags are preset without
  measured justification.
- **5. RocksDB.** Same composite-key scheme. The suffix-edit baseline is one
  `WriteBatch` containing `DeleteRange` over the replaced span (with an explicit
  end sentinel — the binding rejects the unbounded form) **plus the
  reinsertions, committed atomically**; no pre-emptive compaction tuning.
  *Compaction is recorded as an observation* — disk use and tail behaviour
  before/after — not a design mechanism. Its acknowledgement tier is the
  declared per-batch flush policy and is **not equated to checkpoints**;
  WAL/checkpoint flags are documented verbatim, not guessed.
- **6. Segmented binary log.** Records appended to closed-size segments; the
  index (in-memory rebuild or a small sidecar file — the adapter must pick and
  explicitly specify one) maps `(session, ordinal)` to `(segment, offset)`. Edit
  truncates/rewrites the tail from the edit point. Recovery = discard incomplete
  final records, rebuild or repair the index from segment tail on reopen. The
  known risk is edit cost and index/recovery code size; that is what the
  experiment measures.
- **7. SQLite + external payload files.** Small transcript data in SQLite; large
  text payloads in per-session files whose identity/offset rows live in SQLite.
  **Two-store atomicity is the designed risk:** write the payload file first,
  then commit the metadata row; a kill in between leaves an unreferenced (or
  partially indexed) payload that the declared recovery rule must classify as
  unacknowledged and discard or ignore. The adapter must document this ordering
  explicitly.
- **8. Per-session SQLite + coordination DB.** One small SQLite file per session;
  session-internal operations are ordinary **transactional row deletes/inserts**
  (a suffix edit is `DELETE WHERE ordinal>=?` plus reinserts inside one
  transaction), **not whole-file rewrites**. Session deletion = close any open
  clients of that session, then remove the session file **together with its
  WAL/SHM companion files** — a filesystem action, not a cross-database
  transaction. Shared coordination DB updates are separate transactions;
  **SQLite WAL is not atomic across database files**, and no magic cross-db
  atomicity is assumed: the adapter declares which store is authority for each
  record type and accepts bounded cross-store staleness, with the consequences
  verified by the oracle.
- **9. Tsavorite (deferred; standalone not qualified).** The binding review
  found **no official standalone package**: Tsavorite remains tied to the
  actively maintained Garnet *server* line, and its hash-ordered main store
  would require an application-built ordered-index layer to support Urd's
  `(session, ordinal)` windows and suffix edits. The hypothesis stays listed
  with its sketch frozen; it is **deferred out of the execution stages, not
  rejected as technology**, and re-entering Stage 0 needs an explicit owner
  decision plus a re-run of the viability evidence.
- **10. Bounded-read JSONL reference.** Newline-delimited records with backward
  suffix reads; metadata lives in a small separate summary file loaded on
  reopen. Establishes the file-design floor: the earlier store-operations probe
  already exercised append, edit and recovery paths at a reduced surface, and
  this family extends them to full trace scales and the complete operation
  catalog.

### Durability and acknowledgement tiers

Durability levels are **declared, recorded and never treated as equal**. No
adapter presets fsync flags from guesswork or claims a tier it has not
demonstrated.

- Each adapter declares an **acknowledgement boundary** from: (a) memory ack,
  (b) OS-flush ack (data handed to the OS, no fsync), (c) fsync/journal-durability
  ack, with the actual store configuration recorded verbatim.
- Primary comparisons run at **one shared default tier (OS-flush ack)**;
  adapters also record whether a stronger tier is available and what it costs
  in Stage 1 samples. Cross-tier comparisons are recorded as informational, not
  rankings.
- Kill testing (see harness) demonstrates what each store's **documented and
  configured** settings survive at process kill; the settings are recorded
  verbatim, and no report claims a power-loss or durability tier from kill
  results — **the test alone cannot demonstrate one**. At a kill inside an
  unacknowledged atomic operation, the persisted state may be either the
  **before-state or the fully committed after-state**: a commit whose
  acknowledgement was lost can legitimately persist. The oracle accepts both
  outcomes per unacknowledged atomic operation; it never requires that only
  acknowledged operations survived, and never accepts partially applied state
  for an operation whose family declares all-or-nothing atomicity.
- **"No automatic side-effect replay" is a Urd recovery rule for agent work,
  not a property of this storage layer.** The store contract here is narrower:
  a record is either absent or complete, every acknowledged record must replay
  identically after reopen, and recovery must not invent, reorder or re-execute
  tool records. The harness verifies store-level coherence; it does not build
  side-effect replay machinery, and it does not excuse families whose recovery
  repairs themselves into reordered or duplicated history.

## Shared harness contract

### Code ownership and boundaries

- A single harness-owner session owns `experiments/transcript-harness/` —
  runner, generator, oracle, instrumentation and the adapter interface — once a
  shared-code milestone is approved. Owner steering for shared code goes through
  the harness owner; **parallel candidate sessions own exactly one adapter each**
  and change nothing shared. This exists specifically to avoid ten standalone
  frameworks drifting apart.
- Adapter boundary: implement the operation interface over declared storage;
  declare encoding, compression, tier, layout, edit mechanics and recovery rules;
  no logging frameworks, DI containers, config systems, benchmark libraries or
  external tooling. Adapter-specific code stays in its own project directory.
- Source-control discipline: the programme commits harness and adapter source
  only; **raw data, trace outputs, reports and measurement artifacts live in OS
  temp directories**, referenced by paths in summary records. No raw fixtures in
  the repository. Stdout is not evidence: machine-readable result records plus
  verified summaries are.
- Reviewers working via Ollama are read-only over repository documents and
  experiment summaries. **A reviewer's own tool-access limits are not
  repository-wide data bans**; the prior review mistake of treating
  no-access as a global prohibition is recorded here so it is not repeated.
  Reviewers check documents against decisions and contracts; they do not scan
  fixture data.

### Operation catalog

All adapters implement the same logical operations; per-operation parameters are
fixed by the trace. If a family cannot support an operation without weakening a
declared guarantee, that is a finding, not an adapter option.

| Operation | Meaning |
| --- | --- |
| `listSessionsMeta` | Session metadata listing, sized like a tab strip of many sessions. |
| `openRecent(session, 50)` | Newest-window read: the click-to-visible-content workload. |
| `pageOlder(session, before, count)` | Previous-page navigation read. |
| `readRange(session, from, to)` | Bounded range read: the model-context assembly workload. |
| `fetchLargeResult(ref)` | Read one large text tool result in full (hover inspection). |
| `appendMessage(session, parts)` | Incremental append of a message with parts; may include tool calls/results of declared size. |
| `streamPartial(session, chunks)` | Append partial assistant text with explicit `finalize`; unfinalized partials follow the adapter's declared buffering rule. |
| `replaceSuffix(session, ordinal, newMessages)` | Edit an earlier prompt: delete the later continuation and append replacement messages — history replacement only, never rollback of external effects. |
| `createAssignment / settleAssignment` | Bounded assignment/result records so restart persistence and cross-store atomicity are exercised (reduced model, not the full coordination design). |
| `deleteSession(session)` | Final closure deletion, including large payloads. |
| `reopen` / `freshReopen` | Normal reopen after clean close; fresh-process reopen (process exited, store reopened) — the realistic restart path. |
| `crashAt` | Trace marker where the supervisor kills the host process mid-operation. |

### Trace, oracle and fixtures

- **One generated operation trace drives every run.** A deterministic, seeded
  generator produces a trace file describing the interleaved operations above at
  declared scales; traces are regenerated per run, never committed, and every
  adapter consumes the identical trace.
- **One correctness oracle is the authority — and stays outside the measured
  engine.** The runner/oracle and each adapter's store host are **separate
  processes** (the store host is also the kill-test victim). The oracle never
  holds the whole huge dataset inside the store process, and generator, oracle
  and comparison work are **excluded from engine timing and memory figures**.
  Checkpoint verification streams the store's actual full content per session
  or compares incremental digests checked against independently replayed
  expectations; **a digest is only ever computed from verified full content,
  never blindly assumed correct**. After each checkpoint, kill, reopen and
  end-of-trace, runner and oracle compare record content, order, ordinals,
  replacement suffixes, assignment states and final deletions.
  Byte-identical storage is not required; logical equality is.
- **Assignment/deletion consistency is fixed by the contract, not left to
  adapters.** A `deleteSession` operation either (a) is trace-issued only after
  every assignment targeting that session is settled, or (b) explicitly
  cancels/removes the session's outstanding assignment records as part of the
  same declared logical operation. The oracle enforces the declared rule end
  state: **dangling assignment references to deleted sessions are correctness
  failures, not silent per-adapter quirks.**
- **Mixed-scale datasets.** One store instance holds both many small sessions
  and a few huge histories simultaneously (the shared-store case), plus a
  dedicated large-history tier. Scales are declared tiers (e.g. tens of small
  sessions; 10k–100k-record histories; tool results from kilobyte to several
  hundred KiB; at least one session with several large-image references), with
  exact fixture counts recorded per run. Scale choices are workload examples,
  not Urd capacity claims.
- **Images are files with stable references** (agreed direction): the harness
  creates attachment files in a temp managed-attachment directory and stores
  only stable references/metadata in message records, identically for every
  family, so transcript mechanics are what differs. Fixture image bytes are
  synthetic.
- **Large tool results** are deterministic text; a family may store them inline
  or spill them as declared, and the oracle checks identical logical reads.
- **Real user-session material is optional and constrained:** copying real Pie
  session data into fixtures may inform realism, but any such copy is sanitized
  locally (sensitive content excluded), kept in temp, never committed, and the
  original data is only read under a separately approved census/import task. The
  deterministic synthetic generator is always the fallback and must exist first.

### Measurement rules

- **Latency:** fixed sample counts per operation tier (warm repeats plus
  fresh-process samples, per the earlier experiments' convention). Reports give
  **named statistics only**: min, p10, median, p90, max, sample count — with no
  percentiles beyond sample support (no invented p99), no aggregate score.
- **Memory and resources, reported separately, never merged:** managed
  allocations (`GC.GetTotalAllocatedBytes`), private committed bytes and working
  set. GC/native retention after release waits is recorded as allocator
  behaviour, not a leak.
- **Mapping-reservation honesty:** for mmap-based families, virtual address
  reservation is reported separately and is **not** counted as cache budget;
  measured resident set plus explicit cache/disk-traffic observations are the
  comparable figures.
- **Disk footprint:** store size including WAL/journal/segments before, after
  the mixed dataset, after deletions and after any cleanup action (vacuum,
  compaction, segment merge), with the timings of cleanup actions recorded.
- **Code and dependency cost:** adapter physical LOC versus harness LOC reported
  separately; dependency list, package/native-asset sizes and install footprint
  recorded per family. Small-code requirement compliance is measured, not
  asserted.
- **Environment:** all measurements on the same Windows machine in one program;
  background load caveats and non-counterbalanced fixed order recorded, as in
  prior experiments. No machine-wide changes without owner approval.

## Stages and advancement rules

Fixed before any implementation. Caps are stop-and-report limits, not promises;
hitting a cap pauses work for owner steering rather than silently compressing
scope.

- **Stage 0 — viability and correctness smoke (per family).** Adapter compiles
  and loads, implements every catalog operation on a small fixture (~hundreds of
  operations), passes the oracle, and passes a small kill-recovery smoke at the
  declared tier. Suggested cap: a bounded work session per family; an adapter
  is recorded **prototype failed / not qualified**, with evidence, and stops
  there. A failed prototype caps the implementation attempt; it is not proof
  the underlying technology is unviable.
- **Stage 1 — repeated basic operations (survivors).** Full mixed-scale trace,
  warm and fresh-process measurements, footprint including WAL, deletion and
  cleanup, edit and streaming behaviour, all oracle-checked.
- **Stage 2 — justified shortlist, deeper stress.** Concurrency (single process,
  multiple sessions writing while readers read), interruption kills at many
  trace points, and a bounded long-run soak. Only families that Stage 1 justifies
  proceed; roughly two to five.
- **Advancement rule:** a family is dropped from further stages when another
  advancing family is at least as good on every recorded axis (correctness
  incidents, critical-path latency, footprint, code/dependency cost) and better
  on at least one — with margins above declared tie thresholds, so near-ties
  remain ties. **Absent measured values are never grounds for dominance:** a
  family cannot be judged dominated — or dominate — on any axis it has not
  measured in this programme. Owners may keep a dominated family or reinstate a
  dropped one; steering overrides a silent rule, never the reverse.
- **Dependencies:** pinned per the binding review's suggested harness pins
  (`Microsoft.Data.Sqlite` 10.0.14; `LightningDB` ≥ 0.23.1 on the LMDB 1.0
  format; `RocksDB` 11.12.0.7641, never the stale `RocksDbSharp` path; encoding
  set per its table). **Scoped package restores from official feeds into
  repo-local or OS-temp project locations are already approved for this
  programme; no global installs and no machine configuration.** Preparation is
  an untimed, recorded prerequisite — the shared runner implementation proceeds
  under programme approval, with no recurring approval gates. Installation is
  outside measurements. No live model calls and no external tools during any
  stage.

## Metadata-only source census — defined here, executed nowhere in this task

Before any import of real session material ever happens (a later, separately
approved task), the census spec is: **metadata only** — never read message or
tool-result bodies; collect counts, byte sizes, message-per-session distribution
and largest-record sizes; never copy content anywhere; report only aggregate
figures usable to size fixtures. This programme's fixtures use synthetic data
until an owner-approved import task completes under this spec.

## Open items

- **Binding viability is resolved for this stage** by the binding review:
  probed families proceed to Stage 0; LiteDB remains provisional with an added
  first-smoke step; **Tsavorite remains deferred**.
- Harness and adapter LOC budget confirmation once the Stage 0 smoke exists.
- The kill-test supervisor (child store-host process + `TerminateProcess`) is
  part of the shared runner work under programme approval; exact fixture tier
  sizes and trace scales are confirmed when the harness-owner milestone records
  them.
- The whole programme produces evidence, not the storage decision; selection
  remains the owner's, made against [ARCHITECTURE.md](ARCHITECTURE.md)'s
  requirement that decisions state workload, guarantees, rejected alternatives
  and revisit conditions.