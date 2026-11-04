# Storage research

## Status

**Investigation record; the storage decision it informed is now made.**
[ARCHITECTURE.md](ARCHITECTURE.md) owns the agreed requirements and, as of
2026-11-04, the selection: one shared SQLite database for transcripts, session
metadata and assignments, with file-backed attachments. Sections below written
before that date describe the pre-decision comparison; they remain as evidence,
not as a claim that storage is still open.

The reference set is upstream Pi's coding agent, **Pi Durable** (the first-party
upstream package), DeepSeek Harness, Codex and Claude Code. Third-party Pi
rewrites are not substitutes for Pi Durable in this comparison.

The goal is not to copy the most sophisticated harness. Urd needs the smallest
maintainable implementation that supports its behaviour and stays responsive.
Storage format, access pattern, durability policy and application architecture
must be evaluated separately.

## Requirements already established

- Linear conversation history; editing an earlier prompt replaces its later
  continuation rather than preserving an alternative branch.
- Bounded subagents and independent agent-created sessions both remain useful.
- Assignments and results survive backend restarts; persistence is not automatic
  permission to repeat external side effects.
- Minimal delay from clicking a session tab to seeing usable content, with a
  responsive UI throughout the application.
- Reasonable loss of recent progress on a backend crash is acceptable; the saved
  session must remain coherent and continuable after restart. Exact token/time
  loss budgets and elaborate recovery machinery are not requirements.
- Strict code-size restraint, a first-party harness and Windows-first delivery.
- No Pie data compatibility or migration requirement.

Large histories, tool results, multiple active agents and cold sessions belong
in the evaluation. The owner's speculative fan-out examples suggest roughly
50 and 210 live-agent contexts as useful exploratory workloads, not hard limits
or simultaneous-provider-request guarantees. Numeric latency targets remain
open. Recovery mechanics should satisfy practical continuation rather than
inflate the project with stronger guarantees the owner has not requested.

## Data lifetimes for the next comparison

This inventory derives from the decisions in [ARCHITECTURE.md](ARCHITECTURE.md)
and [README.md](README.md); it is not a schema, a new retention policy or evidence
that discovery is complete. Logical ownership does not select a storage layout.

| Data family | Established boundary | Still unresolved |
| --- | --- | --- |
| Readable history | Available through compaction until chat closure; edits replace the later suffix. | Representation, mutation races and interrupted-edit handling. |
| Model context | May be compacted/reduced without replacing the original readable conversation. | Derivation, caching, invalidation and raw-result recall mechanics. |
| Helper/independent history | Bounded helper messages/tool calls remain inspectable until containing-chat closure; independent chats retain their own histories. | Internal representation and detailed cancellation/lifetime interactions. |
| Assignments/shared results | Survive restart; facts still needed by another open session survive one participant's closure. | State transitions, tracking consumers/references and eventual cleanup. |
| Scheduled follow-ups | Survive restart and run automatically when due for open sessions; do not resurrect closed sessions or blindly replay uncertain work. | Delivery and recovery mechanics. |
| Drafts/pending attachments | Simplest ownership; separate client drafts are the working default, with no required cross-client hand-off. | Storage, upload ownership and reload persistence. |
| Tool/attachment detail | Support agreed inspection and retained shared results; no new full-raw-capture guarantee follows from this inventory. | Capture fidelity, payload layout and cleanup ownership. |
| Usage summaries | Compact summaries remain after closure, not conversation/full tool logs. | Exact fields, duration and cleanup timing. |
| Credentials/configuration | Direct provider sign-in and locally managed credentials; configure Urd afresh, not through a Pie importer. | Credential/configuration stores and remote-client authentication. |

Compare designs using cross-lifetime operations, not append speed alone:

1. Close a chat containing a finished helper while another open chat still needs
   a shared assignment/result. Exercise detail cleanup without losing that fact.
2. Restart with a known-undispatched due follow-up for an open chat and a closed
   target. Preserve assignments/results without reopening the closed target or
   treating uncertain execution as permission to replay.
3. Replace an earlier prompt after compaction and tool/helper activity, then load
   visible history cold. Check derived context does not reintroduce the removed
   suffix; do not treat history replacement as rollback of external effects.
4. Snooze, restart and restore an open chat, contrasting its retained history with
   the final-closure cleanup path.

Measure latency and memory numerically even though no hard acceptance thresholds
are selected. Storage read/parse timings are not click-to-visible-content timings.
Representative data volumes, payload ownership and end-to-end behaviour still
need qualification before selecting storage.

## Evidence and reference versions

Inspected on 2026-11-02. Source findings are not benchmarks or proof of delivered
behaviour in every release.

| Reference | Inspected revision / evidence |
| --- | --- |
| Pi coding agent and Pi Durable | [`c9f4c7b9fcd0f3e6b5bbc35c7c9b6cda35580eb8`](https://github.com/earendil-works/pi/commit/c9f4c7b9fcd0f3e6b5bbc35c7c9b6cda35580eb8) |
| DeepSeek Harness | [`ce7073d51254c26cd63b22a60835b8f948deb447`](https://github.com/deepseek-ai/deepseek-harness/commit/ce7073d51254c26cd63b22a60835b8f948deb447) |
| Codex | [`a09812981de703f18f38015cd5d500ab373f616c`](https://github.com/openai/codex/commit/a09812981de703f18f38015cd5d500ab373f616c) |
| Claude Code | Official, dynamically maintained documentation accessed on the inspection date; internal implementation is not established by these docs. |

The Pi embedded in Pie is an older, modified baseline. Pie-specific staging,
write-lease and rewrite machinery must not be attributed to unmodified upstream
Pi. Current Pi Durable is also distinct from the coding agent's default store.

## Source comparison

### Pi coding agent: simple JSONL, whole-session memory

The coding-agent SDK still defaults to `SessionManager`, not Pi Durable.
Sessions use JSONL; opening a session loads entries into in-memory arrays/maps,
and listing sessions reads files to derive metadata. The entry format supports
a parent-linked conversation tree, which Urd does not need to preserve.

This is a relatively direct storage path, but a full-history load does work
proportional to history size before the application can use that representation.
That is an access-pattern observation, not a measured latency claim or an
inherent limitation of JSONL.

Sources: [SDK default](https://github.com/earendil-works/pi/blob/c9f4c7b9fcd0f3e6b5bbc35c7c9b6cda35580eb8/packages/coding-agent/src/core/sdk.ts),
[SessionManager](https://github.com/earendil-works/pi/blob/c9f4c7b9fcd0f3e6b5bbc35c7c9b6cda35580eb8/packages/coding-agent/src/core/session-manager.ts).

### Pi Durable: explicit storage contracts, paged SQLite option

Pi Durable is a separate first-party package, not the coding agent's default
persistence implementation. It provides memory, JSONL and SQLite storage
choices. Its README explicitly warns that its JSONL implementation keeps the
whole history in memory and recommends SQLite for long-running agents.

Its SQLite store has indexed conversation entries and task/submission records,
transactional batches and cursor-based scans. Transcript views can start with
a bounded window; model-context construction still reads the relevant context
range. Display history and model input are therefore different read workloads.

The documented SQLite configuration uses WAL with `synchronous=NORMAL`: process
crash recovery is not the same guarantee as retaining the newest commit after
power loss. Node access uses built-in SQLite through a Chord adapter. Urd can
learn from these boundaries without depending on Pi Durable or adopting its
broader task/document abstractions.

Sources: [README and backend trade-offs](https://github.com/earendil-works/pi/blob/c9f4c7b9fcd0f3e6b5bbc35c7c9b6cda35580eb8/packages/durable/README.md),
[SQLite storage](https://github.com/earendil-works/pi/blob/c9f4c7b9fcd0f3e6b5bbc35c7c9b6cda35580eb8/packages/durable/src/storage/sqlite/storage.ts),
[schema/indexes](https://github.com/earendil-works/pi/blob/c9f4c7b9fcd0f3e6b5bbc35c7c9b6cda35580eb8/packages/durable/src/storage/sqlite/migrations.ts),
[context loading](https://github.com/earendil-works/pi/blob/c9f4c7b9fcd0f3e6b5bbc35c7c9b6cda35580eb8/packages/durable/src/harness/context.ts),
[Node adapter](https://github.com/earendil-works/pi/blob/c9f4c7b9fcd0f3e6b5bbc35c7c9b6cda35580eb8/packages/durable/src/storage/sqlite/node.ts).

### DeepSeek Harness: canonical event log, separate projections

The inspected base composition uses JSONL session persistence and JSON-backed
domain storage, including projection checkpoints. Its optional SQLite search
index is configured not to open in that composition; a SQLite KV backend is a
separate option, not evidence that session logs use SQLite by default.

The client asks for a turn-aligned history window, but a cold query observation
loads the underlying event log before paging its in-memory events. A paged UI
response therefore does not establish bounded cold-storage work. Prepared
observations are cached, so warm and cold opens have different paths.

Session persistence batches writes and provides a flush barrier. The JSONL
provider documents fsync before append completion and truncation rollback on
failed batches. Large tool outputs can spill to files with previews/references;
attachments are also externalized. Its production provider includes format
migration, compression and publication machinery: JSONL does not automatically
mean a tiny implementation.

Sources: [base composition](https://github.com/deepseek-ai/deepseek-harness/blob/ce7073d51254c26cd63b22a60835b8f948deb447/packages/bundle/base/cordis.patch.yml),
[client session open](https://github.com/deepseek-ai/deepseek-harness/blob/ce7073d51254c26cd63b22a60835b8f948deb447/packages/api/session-controller/src/client/sessions/session.ts),
[cold read](https://github.com/deepseek-ai/deepseek-harness/blob/ce7073d51254c26cd63b22a60835b8f948deb447/packages/session-query/session-query/src/cold-read.ts),
[JSONL provider](https://github.com/deepseek-ai/deepseek-harness/blob/ce7073d51254c26cd63b22a60835b8f948deb447/packages/session/session-persistence-jsonl/README.md),
[large-output storage](https://github.com/deepseek-ai/deepseek-harness/blob/ce7073d51254c26cd63b22a60835b8f948deb447/packages/spill/spill-local/README.md).

### Codex: JSONL history plus SQLite metadata and read projections

Codex's local thread store treats rollout JSONL as canonical conversation
history. SQLite supports queryable metadata and, in the paginated path, a
separate materialized history projection with transactionally updated
checkpoints. Some metadata updates are SQLite-only; not every database field
can be described as a disposable copy of the transcript.

The paginated writer flushes rollout data before advancing its projection.
That establishes ordering between stores, not an fsync guarantee: the inspected
JSONL path calls `flush`, not `sync_all`. Writers also use per-thread locks.
Model-context loading and paginated UI history have separate read paths;
compacted context can be found through a reverse rollout scan.

This is useful evidence that raw history and responsive query views can coexist,
but Urd would have to justify the dual-write, indexing, repair and locking costs.
Codex's need for multiple stores does not establish that Urd needs them.

Sources: [thread-store responsibilities](https://github.com/openai/codex/blob/a09812981de703f18f38015cd5d500ab373f616c/codex-rs/thread-store/README.md),
[write ordering](https://github.com/openai/codex/blob/a09812981de703f18f38015cd5d500ab373f616c/codex-rs/thread-store/src/local/live_writer.rs),
[history projection](https://github.com/openai/codex/blob/a09812981de703f18f38015cd5d500ab373f616c/codex-rs/thread-store/src/local/thread_history.rs),
[model-context loading](https://github.com/openai/codex/blob/a09812981de703f18f38015cd5d500ab373f616c/codex-rs/thread-store/src/local/model_context.rs),
[SQLite settings](https://github.com/openai/codex/blob/a09812981de703f18f38015cd5d500ab373f616c/codex-rs/state/src/sqlite/connection.rs).

### Claude Code: documented JSONL, implementation limits

Official documentation describes continuously saved per-session JSONL,
separate subagent transcripts and file-backed large tool results/attachments.
Resume restores the relevant conversation and compacted context; interrupted
tool calls are not automatically rerun. Hook documentation warns that transcript
writes are asynchronous and may lag the live conversation.

These documents do not establish the entire internal indexing architecture,
all storage dependencies, flush/fsync guarantees or actual tab-load latency.
JSONL documentation is not proof that no other store is used. Its SDK's external
`SessionStore` interface is also not a description of every CLI persistence path.

Sources: [session management](https://code.claude.com/docs/en/sessions.md),
[data layout](https://code.claude.com/docs/en/claude-directory.md),
[subagents](https://code.claude.com/docs/en/sub-agents.md),
[hook transcript timing](https://code.claude.com/docs/en/hooks.md),
[SDK session storage](https://code.claude.com/docs/en/agent-sdk/session-storage.md).

## What this evidence does and does not establish

- **Whole-history reconstruction is a risk to cold-open latency.** It is not
  required by every file-based design, nor prevented merely by choosing SQLite.
- **Paged display and model context are different workloads.** Opening a tab
  should not automatically be equated with preparing a model request.
- **Large payload handling matters independently of the store.** Loading every
  image or full tool result to display a session can defeat a good index.
- **Durability policies must match before comparing write speed.** Buffered
  writes, flushes, fsyncs and multi-record transactions promise different things.
- **Hybrid storage has a code cost.** A rebuildable projection still needs
  ordering, invalidation and recovery rules. It is not free performance.
- **Source popularity is not a benchmark.** No inspected source proves which
  candidate is fastest, smallest or cheapest to maintain for Urd.

## Local read-path experiment

### Method and scope

A synthetic Windows x64 probe used Node 24.18.0 and built-in SQLite 3.53.1 on a
Ryzen 9 6900HX machine with approximately 15.3 GiB usable RAM. This is a research
runtime, **not a Urd language or dependency decision**.

Three linear sessions contain 1,000, 10,000 and 100,000 deterministic records
with roughly 1 KiB of mixed ASCII/non-ASCII text per record. Each read returns
the same newest 50 decoded records in chronological order. Extra fixtures cover
three-record history and a 256 KiB record crossing multiple read chunks.

Compared paths:

- **Full JSONL:** read, decode and parse the entire file, then select 50 records.
- **Suffix JSONL:** seek backwards in 64 KiB chunks until enough complete lines
  are present, then decode and parse only the requested records; no sidecar index.
- **Indexed SQLite:** a rowid table with `session`, `ordinal` and JSON `payload`,
  indexed uniquely on `(session, ordinal)`, queried with
  `WHERE session = ? ORDER BY ordinal DESC LIMIT 50`, then decoded and reversed.

Setup is outside timings. Timings end at the decoded ordered result; handle
cleanup is outside the timed region. Twenty repeated samples were taken per
case. SQLite was tested both reopening/preparing per read and retaining a
connection/prepared statement; the main table uses the retained path. JSONL
opens the file per read. These are not decoded-session cache hits.

Five additional samples per case used a fresh application process, including
storage open/prepare but excluding process startup. **Neither profile is cold
disk:** the OS cache is uncontrolled and was warmed by setup and validation.

### Results

Median repeated-read milliseconds; newest 50 records only:

| History | JSONL file size | Full JSONL parse | JSONL suffix read | SQLite, retained connection |
| --- | --- | --- | --- | --- |
| 1,000 records | 1.07 MiB | 7.14 | 0.28 | 0.45 |
| 10,000 records | 10.73 MiB | 55 | 0.28 | 0.44 |
| 100,000 records | 107.44 MiB | 545 | 0.27 | 0.42 |

For the 100,000-record session, fresh-process medians were approximately
509 ms, 0.56 ms and 1.38 ms respectively; SQLite includes connection and query
preparation on that path. The suffix reader retrieved 64 KiB rather than the
107 MiB file. With the 256 KiB record inside the requested window, repeated
suffix reads took 1.62 ms and retained SQLite reads 2.29 ms: record count alone
is not a byte or decode-cost bound.

These results show the cost of **whole-history loading versus bounded reads**
in this fixture, not a meaningful overall victory for files or databases.
Both bounded paths were sub-millisecond for the ordinary latest-50 window.

### Verification and limits

Every path, including retained SQLite, was checked against independently
regenerated expected records and against the other paths: full decoded content,
ordering, record counts and session identity. The large-line and UTF-8 boundary
cases passed. The SQLite query plan confirmed use of the composite index.
The full fixture set occupies approximately 119.6 MiB of JSONL and 147.1 MiB in
the chosen SQLite schema; these sizes are schema-specific, not format minima.

Temporary script, raw samples, hashes and method notes are retained under
`%TEMP%\urd-storage-probe-Qwkxlw\` for this investigation, not committed as
application code. They are disposable local artifacts, not durable release
acceptance evidence. Re-run an appropriately scoped benchmark before making
release claims or transferring these numbers to another stack or workload.

The experiment does **not** measure:

- End-to-end UI, transport, rendering, process startup or model-context assembly.
- Previous-page navigation, turn-aligned windows, compaction or search/indexing.
- Concurrent append/read/edit activity, persistent assignments or safe deletion.
- Crash consistency, fsync policy, torn/incomplete logs or power-loss durability.
- Production storage-layer LOC, dependency size or release-packaging overhead.

Files were complete and newline-terminated; the suffix reader is not a proposed
production reader with partial-write recovery. All operations were serial and
synchronous. Memory observations are too approximate for a comparative memory
budget. The reference harnesses themselves were not benchmarked.

## Minimal store-operations comparison

A second, separate probe compared small first-party implementations of the
operations Urd needs, rather than isolated transcript reads:

- **Files:** one JSONL conversation per session, plus canonical JSON session and
  task metadata loaded into memory. Recent windows scan backwards; appends update
  the log and atomically replace the summary file; edits replace the affected
  conversation file. Reopening reconciles summaries against log tails.
- **SQLite:** indexed session, message and task tables, retained statements and
  transactions for conversation-plus-summary changes, using WAL/NORMAL.

Both used Node 24.18.0 / SQLite 3.53.1 on the same machine as the first probe.
Fixtures had 50 or 210 session identities with 12 approximately 1 KiB messages
and one assignment per identity, plus a separate 50,000-record long history.
These represent **storage requests from many identities, not live agent load**.
Task participants and conversation records are deliberately simplified; this is
not a complete execution, permissions or tool-call schema.

Selected median milliseconds for the 210-identity fixture:

| Operation | Samples | Files | SQLite |
| --- | --- | --- | --- |
| Read recent window | 20 | 0.17 | 0.07 |
| Append message and update summary | 20 | 1.91 | 0.23 |
| Update assignment/result | 20 | 1.59 | 0.13 |
| Replace suffix of a 12-record history | 20 | 3.42 | 0.63 |
| Reopen store, list sessions, read recent window | 10 | 42.98 | 1.22 |

Warm operations exclude construction/preparation. The reopen row includes those
costs but not Node startup; filesystem caches were uncontrolled and likely warm.
Samples were serial and alternated between stores. In the separate 50,000-record
history, newest-window reads took approximately 0.26 ms for files and 0.10 ms
for SQLite. Long-history **edits** were not timed.

### What the prototype adds to the comparison

Both bounded read paths remain fast. In these implementations, the more material
differences were metadata updates and reopening: the file implementation rewrites
shared JSON metadata and inspects session tails, while SQLite updates indexed
rows and coordinates related changes in a transaction.

The formatted prototypes contain 251 and 205 physical lines respectively,
including blanks; test/fixture/measurement code is separate. Those counts are
not production-size estimates or proof of maintenance superiority. The useful
comparison is the machinery each design needs: custom file publication, tail
repair and summary reconciliation versus schema/indexes, transactions and a
database runtime. Built-in SQLite required no npm dependency in this experiment;
the dependency and packaging cost for Urd's eventual runtime is not yet measured.

The file design is not an optimized lower bound. Per-session metadata, batched
summary writes, sparse offsets or segmented logs could change its costs; they
were not benchmarked. Conversely, there is no reason yet to adopt Codex-style
dual canonical-log/read-projection machinery merely because Codex uses it.

### Correctness and evidence limits

Tests compared ordered message windows, summaries, task queries/results,
suffix replacement and reopen/continue across the implementations. Controlled
child-exit probes also verified:

- A complete JSONL append before summary publication is reconciled on reopen;
  the following append receives a new sequence number.
- An incomplete final JSONL line is discarded while earlier records remain
  readable and further appends work.
- An uncommitted SQLite metadata change is rolled back on reopen.

These are storage-level checks, not proof that an actual agent can recover from
every interrupted tool interaction. No live tools or model requests were run.
No concurrent writers, power-loss tests, large attachments or end-to-end UI
were covered. Post-review recovery checks passed on the benchmark's Node 24.18.0
runtime; timings were not remeasured for formatting and recovery-only fixes.

The temporary sources and results are in
`%TEMP%\urd-store-operations-esszfN\`. About 120 MiB of synthetic artifacts were
created. As with the first probe, these local artifacts are not durable release
acceptance evidence or application implementation.

## Candidate assessment — superseded by the 2026-11-04 selection

**The owner selected shared SQLite as the working storage design on
2026-11-04**; the selection, rejected alternatives and revisit conditions are
recorded in [ARCHITECTURE.md](ARCHITECTURE.md). The assessment below is the
evidence that led there, kept as written at the time.

The research recommendation was SQLite for structured session and coordination
state, with files available for large artifacts. Its case is not uniquely fast
tab loading—bounded file reads also performed well—but potentially avoiding
custom indexes and cross-file consistency machinery as metadata, edits and
persistent assignments are added.

A narrow first-party storage owner remains preferred over an ORM,
event-sourcing platform or interchangeable-backend framework unless a concrete
requirement justifies it. Do not depend on Pi Durable or copy another harness's
entire storage architecture. Schema, driver, process placement and final
artifact policy are ordinary design work under the recorded decision.

## Remaining decision evidence

The items below are no longer pre-selection gates; they are adopted into the
decision's revisit conditions and the experiment programme's remaining
validation stages. Before treating the selection as fully validated, resolve
or test:

1. The simplest persistence/recovery mechanics that leave a coherent,
   continuable saved session after a crash while permitting recent progress loss;
   do not invent strict per-token durability or loss-time budgets.
2. History-size distributions and active/waiting proportions within the
   speculative 50/210-context examples, including model-context construction
   as well as browsing. Storage requests from that many identities are not
   themselves a benchmark of that many live agents.
3. Append, edit/truncate and assignment-update behaviour under equivalent
   durability requirements, including interrupted writes and uncertain effects.
4. Metadata listing/search, paging cursors and large-payload lifetime.
5. End-to-end UI timing under active streaming, not only storage query latency.
6. The smallest viable implementation and its dependencies/packaging cost.

The decision recorded on 2026-11-04 states its workload and guarantees,
the alternatives rejected, and the evidence that would justify revisiting it.
Do not respond to the settled engine question by maintaining multiple
production backends.
