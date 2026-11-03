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
- **A separate parallel session is surveying binding/dependency viability**
  (SQLite drivers, LiteDB, LMDB, RocksDB, Tsavorite status on .NET). That
  survey reports separately; this programme must **not duplicate it**. Where a
  family's viability depends on that survey, this document marks the dependency
  "pending survey" instead of guessing.

## Candidate families (hypotheses)

The families below are hypotheses to exercise, **not commitments**. A family
that Stage 0 shows to be unviable is recorded as unviable with evidence and
dropped; dominated families may also be dropped (see advancement rules). The
ten families deliberately span shared databases, per-session databases, binary
logs, files and hybrids; the owner wants broad exploration before convergence.

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
| 4 | LMDB indexed records | Single LMDB environment; keyed binary records, read-mostly B-tree/bitmap index in a control database. |
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
  crash behaviour is measured, not assumed.
- **4. LMDB.** Records keyed by `(session, ordinal)`; the index is part of the
  data. Edit writes the replacement suffix as new keys and publishes an updated
  live-top pointer in a single small control entry, with the replaced range
  cleaned up later; **write-multiply and mmap memory behaviour must be measured
  (see resource rules)**. Recovery = LMDB's single-writer transactional
  semantics on reopen.
- **5. RocksDB.** Same key scheme; LSM compaction behaviour affects deletion
  footprint and tail latency — both measured, including before/after-compaction
  disk use. Edit via new keys plus a tombstone/manual compaction policy;
  checkpoint/flush boundaries define its acknowledgement.
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
- **8. Per-session SQLite + coordination DB.** Session-internal operations are
  atomic within one session file; shared coordination DB updates are separate
  transactions. **SQLite WAL is not atomic across database files**; the adapter
  must define ordering rules (which store is the authority for each record type)
  and accept bounded cross-store staleness. Edit and delete are per-session-file
  rewrite operations; recovery is per-file SQLite recovery plus an index repair.
- **9. Tsavorite.** Gated: proceed only when the parallel binding survey shows a
  maintained, installable, reasonably sized .NET integration with a workable
  crash-checkpoint story. Otherwise record unviable with the survey's evidence.
- **10. Bounded-read JSONL reference.** Newline-delimited records with backward
  suffix reads; metadata lives in a small separate summary file loaded on
  reopen. Establishes the file-design floor the earlier probes observed, now
  with a complete edit/deletion/recovery surface instead of the probe's read-only
  approximation.

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
- Kill testing (see harness) demonstrates what each declared tier actually
  survives at process kill. **A kill is process death, not power loss**; results
  never claim power-loss durability.
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
- **One correctness oracle is the authority.** An independent in-memory reference
  model replays the trace and records expected state after every checkpoint.
  After each checkpoint, kill, reopen and end-of-trace, runner and oracle compare:
  record content, order, ordinals, replacement suffixes, assignment states and
  final deletions. Byte-identical storage is not required; logical equality is.
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
  failing correctness after its repair budget is recorded **unviable with
  evidence** and stops there.
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
  remain ties. Owners may keep a dominated family or reinstate a dropped one;
  steering overrides a silent rule, never the reverse.
- Dependencies installability/versions: confirmed by the parallel binding survey
  and an approved, pinned, offline-verified preparation step before Stage 0
  (matching the earlier programme's prepare-and-pin rule). Installation is
  outside measurements. No live model calls, no external tools, no machine-wide
  configuration during any stage.

## Metadata-only source census — defined here, executed nowhere in this task

Before any import of real session material ever happens (a later, separately
approved task), the census spec is: **metadata only** — never read message or
tool-result bodies; collect counts, byte sizes, message-per-session distribution
and largest-record sizes; never copy content anywhere; report only aggregate
figures usable to size fixtures. This programme's fixtures use synthetic data
until an owner-approved import task completes under this spec.

## Open items

- Pending sibling **binding-viability report** (families 3, 4, 5, 7, 9 gate on
  it; Tsavorite is explicitly feasibility-gated).
- Harness and adapter LOC budget confirmation once the Stage 0 smoke exists.
- Kill-test host/supervisor mechanism confirmation (child process +
  `TerminateProcess` is the default plan) on the Windows machine.
- Fixture tier sizes and trace scales are proposals until the harness-owner
  milestone records exact figures.
- The whole programme produces evidence, not the storage decision; selection
  remains the owner's, made against [ARCHITECTURE.md](ARCHITECTURE.md)'s
  requirement that decisions state workload, guarantees, rejected alternatives
  and revisit conditions.