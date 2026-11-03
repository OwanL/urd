# Lightweight foundation experiments — proposal

Status: proposed, not approved for execution. Prepared after three bounded design
reviews of workload coverage, maintenance/portability, and experiment scope.
No prototypes, installations or benchmarks were performed for this proposal.

## Aim and limits

Obtain useful heuristics about ordinary Urd work, not a benchmark suite or a
miniature production Urd. Compare equivalent observable behaviour, retain raw
observations, and explain costs rather than manufacture an overall score.
The existing [finalist measurement contract](FOUNDATION-RESEARCH.md#bounded-measurement-contract-for-finalists)
is the deeper follow-up, not the instrumentation requirement for this first pass.

**Recommended first batch:** TypeScript/Node + Hono + AI SDK; Go + standard HTTP
or Chi + Fantasy; C#/.NET JIT + Minimal APIs + Microsoft.Extensions.AI. These
contrast implementation/toolchain approaches, not proven winners. Try Bun and
Deno against the same TS implementation as cheap compatibility checks; record
necessary changes rather than silently substituting dependencies.

Python/PydanticAI, Kotlin/JVM and Rust remain challengers. Reuse the frozen tasks
for a second batch where results leave them plausible; omission from the first
batch is not evidence against them. No final technology choice should be made
merely because three candidates were implemented first. UI frameworks, desktop
hosts, storage engines and backend languages are not a Cartesian test matrix.

## Four tasks

| Task | Small representative exercise | Observe |
| --- | --- | --- |
| **1. Open and switch sessions** | Start backend, show session list, open newest 50 messages, fetch an older page, switch sessions and inspect one large tool result. Use histories of 100 and 10,000 roughly 1 KiB records plus one 256 KiB result. | Startup-to-ready; first and repeated click-to-rendered-content; backend CPU/memory and loaded-data behaviour. |
| **2. Run, delegate and interrupt work** | One streamed turn requests a tool, executes it and streams a final answer through the chosen model library. Then one parent launches four bounded mock helpers. Observe independent progress; cancel one running tool, cancel another before tool admission, disconnect/reconnect the client, finish work and explicitly close test contexts. | Streaming gaps; cancellation-to-child-exit; CPU/memory at idle, during work and after quiescence; amount of coordination/glue. |
| **3. Make a maintenance change** | After the baseline works, add an optional per-tool deadline, propagate it from the request to execution, use the cancellation path and report a timeout result. Test normal completion and timeout without disturbing siblings. Rebuild and restart. | Changed files/owned code, tests needed, source/build friction, retries and active effort; whether cancellation logic was reusable. |
| **4. Check the OS path** | For each stack, list actual runtime/native dependencies and Windows/macOS/Linux build, packaging and integration paths. Run the probe on Windows. For up to two informative candidates, repeat source build and non-GUI tasks on actual macOS, then Linux, when hosts are available. | Executed versus documented support, platform-specific changes, prerequisites and missing capabilities. No invented portability score. |

The fixture sizes are convenient synthetic inputs, not production requirements.
Use 1 context first, then 50 open contexts with only four active streams. Report
what is actually hydrated; a session index is not 50 loaded model contexts. Make
the contexts' data independently identifiable. Defer 210 contexts until smaller
runs reveal a useful question; never equate 210 identities with 210 active agents.

For task 1, use fixed pre-sliced JSON history pages as **test inputs**, not a chosen
storage architecture. Do not implement a database or load the whole history just
to retrieve a page. This checks hydration, decoding and presentation, not storage
engine performance or realistic persistence. Model input uses a fixed bounded
context, separately from the readable history. No summarising model is needed.

For task 2, use a local canned provider-format stream compatible with the selected
libraries, not live accounts. Include tool arguments/results, not just string
concatenation. One paced replay represents normal I/O waiting; one small no-delay
replay exposes processing overhead. Neither establishes maximum throughput.
Use one harmless owned subprocess fixture that emits output and waits. Common
fixture helpers are test infrastructure, not implied production dependencies.
No full process-tree manager, scheduler or durable delegation system is required.

Minimal behavioural checks: ordered/session-isolated output; tool/result linkage;
no effect from a tool cancelled before admission; running child really exits;
siblings continue; client disconnect does not stop authorised work; no late result
duplicates after cancellation/timeout. These are validity checks, not a complete
Urd acceptance suite. Cancelling work does not undo effects already performed.

## Minimal shared scaffolding

- One deterministic data generator and local fake-provider/helper fixture.
- One small plain browser page shared by all backends: session selection, message
  view, result inspection and run/cancel controls. It does not select a UI toolkit.
- A handful of behavioural checks and an existing OS process sampler. Use simple
  scripts/commands and CSV or JSON observations, not a runner framework or dashboard.
- Same fixtures, requests and expected outcomes; idiomatic libraries/implementations
  may differ. Do not impose identical internal allocation or concurrency strategies.

Source and build development can be delegated in parallel with separate owned
directories/ports. Measurements run serially on the same machine; record background
load and rerun conspicuous noise rather than stopping unrelated applications.
Use pinned versions and ordinary production/release settings, without bespoke tuning.
Record one fresh-process run and three repeated runs per scenario, alternating
candidate order. Preserve each value and report median/range where useful, not
percentiles or statistical significance from this tiny sample. Fresh process does
not mean cold filesystem cache. Label DOM/frame timings as paint approximations
where used; backend response time is not visible-content time.

Record backend CPU time, OS private committed memory/working set where available,
peak and post-quiescence readings. Label metrics exactly and keep browser/tool/
driver processes separate; do not sum shared working sets as unique physical RAM.
Keep the fake provider outside application totals and identify fixture helpers.
A production-required helper still counts as an application cost. Runtime-specific
GC/allocation tracing is follow-up only if it explains an important difference.

## Maintenance and portability evidence

Count application, test, shared-scaffold and generated code separately. Describe
direct dependencies and required native/runtime helpers; package counts alone are
not maintenance scores. Record initial setup/build and incremental edit/test/build
steps separately from downloads. An existing developer machine is not proof of a
clean-machine install. If setup fails or would need significant machine changes,
record the blocker and seek approval rather than modifying the machine implicitly.

Effort/retries are anecdotes influenced by the agent, tooling and familiarity;
record those conditions. Neither a short implementation nor a slow first attempt
proves long-term maintenance cost. Independent review should check that shortcuts
did not make one implementation's task easier.

OS evidence labels: **executed**, **documented only**, **blocked/unavailable**.
A Mac browser using a Windows backend or a cross-compiled binary is not a macOS
backend test. Headless CI can establish build/non-GUI behaviour, not interactive
permissions or desktop automation. Minimum OS versions, Linux distributions and
X11/Wayland coverage remain open; record tested environments without generalising.
Audit credentials, process cancellation, background controls, browser drivers and
desktop automation paths early, but do not implement every OS adapter in this pass.

## Budget, stop rules and next steps

Suggested caps for approval, **not duration estimates or performance budgets**:

- Shared scaffolding: at most two hours of active implementation effort; if it
  needs a general runner, extensive abstractions or a dashboard, simplify instead.
- Each initial stack: at most three hours of active implementation/checking,
  including the small maintenance change. Pause and report unfinished work at the
  cap; do not spend days tuning or solving an ecosystem issue silently.
- Each TS runtime variant: one short compatibility attempt (up to 30 minutes),
  not a fork/rewrite. Timeout or failure is unqualified evidence, not elimination.
- Additional OS execution: up to one hour per candidate/OS with an available host;
  missing machines, permissions or toolchains are reported, not worked around by
  unapproved infrastructure provisioning. Downloads/waits remain visible, and
  blocked work is paused rather than allowed an unlimited calendar-time extension.

Defer real provider sign-in, paid calls, full compaction/restart/retention rules,
installer/signing implementation, Linux distro matrices, soak tests and UI/host
combinations. Existing storage probes inform later work; do not duplicate them.

Before final selection, separately check one shared view in VS Code and a small
number of cross-platform desktop hosts, plus the chosen browser/MCP and desktop
integration paths. Those cannot be inferred from this backend exercise. Do not
multiply this first batch by UI toolkit/host combinations.

Deliver one compact comparison table plus reproducible commands, pinned versions,
raw samples and caveats. Large repeated differences or demonstrated integration
costs guide the next check; small/noisy differences remain ties. A failed fixture
requires diagnosis before blaming the stack. Timebox exhaustion is a cost signal,
not proof of incompatibility. Present results and propose the next small batch;
do not automatically expand the experiment or select the foundation.
