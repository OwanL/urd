# Model/tool execution: decision and qualification

**2026-11-04.** Bounded design work, not a production implementation or application
class/file layout. `IChatClient` / `Microsoft.Extensions.AI.Abstractions` remains
selected. Only two loop candidates were compared: a small Urd-controlled cycle and
`FunctionInvokingChatClient` (MEAI 10.11.0).

## Recommendation and owner decisions

**Use a narrow Urd-owned cycle; do not add full MEAI for its function loop now.**
Both candidates met the exercised control needs without a library fork. The
library saved only **20 nonblank/noncomment lines in the disposable comparison**,
not a demonstrated production-maintenance advantage. Direct control and avoiding
an optional dependency are the deciding preferences—not a claim the library is
incapable or that production replacement costs only 20 lines.

The owner, after a plain-language comparison, said:
> saving 20 lines is not worth it. If we can ommit a dependency with 20loc then we
> should do so in my opinion. What do you think? feel free to push back

The recommendation was changed from a slight library lean to Urd ownership, with
an explicit correction that the number describes incomplete probes. Reconsider
if actual implementation starts recreating substantial library machinery. Reuse
Abstractions' message/stream helpers; do not hand-write provider parsing merely
to own the cycle. No production scaffold was authorized.

**Discovery: qualified native additions, otherwise a stable wrapper.** The owner
proposed, and this stream accepted:
> for cases where the provider doesnt have an implementation, we relly on the
> wrapper fallback, otherwise it gets omitted from the initial tool list as it
> wont be needed.

Consequences:
- Where provider **and adapter** support qualified native appended definitions,
  use them and omit the invocation wrapper from the initial catalog.
- Otherwise include the wrapper **from the start**, and disclose recovered tools
  through appended context. Do not add the wrapper late and pretend the prefix
  stayed unchanged. This replaces this stream's proposed ordinary-list fallback;
  ordinary-list updates are an alternative, **not the selected fallback**.
- Retain AI catalog pruning, excluding protected core/pinned tools and skills
  before selection. Recovery complements it. Initially selected native tools and
  protected core capabilities need not all become wrapper calls.
- Both exposure routes use the same live tool resolution, validation, permissions
  and execution. Historical exposure never grants current authority. Record the
  outer provider call ID and resolved tool identity distinctly for wrapper calls.

This favours prefix stability, not proven cache savings. Wrapper reliability/cost
in Urd remains untested. No universal discovery framework or Claude beta adapter
is approved. Exact exposure/reset policy on provider switches remains to design;
a rejected native request is not permission to silently switch to an unadvertised
wrapper or rerun tools.

## What was actually exercised

Earlier [Go/.NET discovery](GO-DOTNET-DISCOVERY.md) ran real loop libraries with
**in-process fake models**. Earlier [model-interface research](MODEL-INTERFACE-RESEARCH.md)
only round-tripped native SDK JSON. Both artifact sets survived and were reused:
source patterns, exact cached dependencies and raw-item reconstruction. They were
not mistaken for new HTTP or provider evidence.

New work used .NET SDK **10.0.403**, runtime **10.0.14**, MEAI **10.11.0**, and
OpenAI **2.14.0** on Windows. No login, credentials, live models, metered calls,
machine-wide installs or production builds. Builds/runs were sequential under
`urd:heavy-local-work`; the final pre-build snapshot was 1,201 MiB free RAM.

### Matched scripted-IChatClient comparison: 63 assertions passed

Both paths shared the fake model, harmless counted-effect tools, admission/save
boundary and assertions. These are functional tests, not latency/RAM benchmarks.

| Scenario | Observed in both candidates |
| --- | --- |
| Stream → tool → continuation | Two model requests, one effect; matching call/result IDs in continuation and emitted results; final text and response/message IDs present. |
| Cancel queued for model admission | No provider/model call and no tool effect. |
| Cancel while waiting on model | Cooperative cancellation; one model call, no continuation. |
| Cancel in cooperative tool | Effect counter already incremented; no continuation. Cancellation did **not** mean no effect. |
| Client detaches | A separately cancelled channel reader left the backend pump running; backend completed. This is token-ownership evidence, **not an actual browser/HTTP disconnect test**. |
| Ordinary model/tool exception | Explicit terminal-error policy surfaced failure without retry or another model call. |
| Recording fails **after** effect | One effect, failed simulated save sees matching call/result history; next model request prevented. No rollback or database-durability claim. |
| Error content / Length / EOF after complete-looking call | Common model boundary rejected unsuccessful or missing terminal evidence; neither loop executed the call. |
| Provisional steering within a two-tool batch | Second unstarted call skipped with an explicit matching nonexecution result; steering retained through another tool round. Feasibility experiment, **not adopted steering policy**. |

Library-specific observations:
- `AdditionalTools` really invokes a named function while `ChatOptions.Tools`
  remains empty. Native appended discovery does **not** inherently need a library
  fork or duplicate top-level tool definitions.
- Default tool exceptions become a generic error result and permit continuation.
  The matched terminal-error case explicitly set the error limit to zero. Neither
  choice is the final Urd ordinary-tool-error policy; useful model recovery from a
  routine tool error must not be confused with retrying uncertain effects.
- `MaximumIterationsPerRequest=3` produced **four** requests: three tool rounds,
  then a final request with callable tools removed. Default 40 produced **41**.
  The custom fixture instead stopped at three requests without changing the tool
  list. This is a real behaviour difference, not a hidden equal-cap comparison.
- A model-request middleware gate can stop continuation on recording failure.
  Putting a fatal recording failure in an ordinary tool-error observer is not
  equivalent: the library can convert tool failures into model-visible results.

Neither fixture is a production-complete loop: concurrent-tool policy, approvals,
noncooperating commands, full validation, recovery and final-response persistence
still need implementation tests. A tool-result stream update is not a durable
receipt; even the final successful assistant response must be recorded separately.

### Real adapter + local HTTP/SSE + file reload: seven requests passed

This is **adapter-level scripted transport evidence**, not a live-provider round
trip. [Provider Access](PROVIDER-QUALIFICATION.md) selected public ChatGPT-plan
Responses as the representative route (owner: either ChatGPT or Ollama is fine).
Its current restrictions require `store:false`, `stream:true`, full input history,
no `previous_response_id`, and functions in namespaces or `additional_tools`.
The fixture prioritised **additional_tools**, not namespace support.

| Item | Concrete observation / required owned glue |
| --- | --- |
| Request flags | Captured all seven requests with store false, stream true, encrypted-reasoning include, no previous-response/conversation ID. No live acceptance or exhaustive forbidden-option validation. |
| Native tool exposure | Ordered `additional_tools` JSON, native developer role and synthetic item ID survived adapter serialization and reload at the same position. The MEAI carrier must be assistant-role in this path; developer-role raw content silently disappeared in the conversion negative control. This carrier is an adapter workaround, not a user-visible assistant message. |
| Function identity | `call_id`, name and parsed arguments survive. Streaming `FunctionCallContent` loses native function-item ID/raw representation. Capture `StreamingResponseOutputItemDoneUpdate.Item` **before discarding/coalescing updates**, then retain/reconstruct that native JSON. |
| Persistence | Default `ChatMessage` JSON drops `RawRepresentation`; subsequent wire request loses raw exposure/unknown items. Explicit indexed native JSON reconstruction restores IDs, payload and order. This uses two fixture files, **not atomic SQLite storage, crash recovery or a proposed production sidecar**. |
| Reasoning | Encrypted reasoning and reasoning-item ID already survive ordinary message JSON through `ProtectedData` / `AdditionalProperties`; do not duplicate everything unnecessarily. |
| Result | Continuation retains matching call/result ID. The adapter JSON-encodes a string result (wire output includes JSON quotes); the fixture checks the decoded value. |
| Usage | Scripted input/output/total 30/9/39 and cached input 12 surfaced. These values were injected, not measured savings. |
| SSE error event | Emits `ErrorContent`, not a thrown exception. |
| failed / incomplete / clean EOF | Each left a complete-looking function, **null generic finish reason, no ErrorContent and no exception**. Native typed terminal evidence distinguishes failure/incomplete from missing completion. The fixture gate required completed and rejected all three before its synthetic execution counter. |

The adapter fixture's `tool_search_output` is **unknown-item codec stress only**;
ChatGPT-plan currently forbids `tool_search`, including client execution. It is
not a valid plan-route request/response example. Synthetic IDs/payloads accepted
by the local fixture do not prove that every field is accepted by OpenAI.

**Critical integration rule:** successful enumeration, HTTP 200, tool-item completion
or parsed arguments alone must not authorize execution. Check the chosen route's
positive completion and failures before dispatch. Both loops need this; a generic
loop does not repair a lossy adapter. The original fixtures validated the native
guard and loop separately. The bounded composed follow-up below closes that
specific local integration gap, not production or live-provider qualification.

### Composed follow-up: adapter → guarded Urd loop → SQLite → continuation

**2026-11-04: 47 assertions passed across five scenarios / six local HTTP requests.**
This newly authorized disposable experiment links the **unchanged prior `UrdLoop.cs`**,
uses the real pinned Responses `IChatClient` adapter, and invokes a harmless counted
`AIFunction`. Same SDK/runtime/MEAI/OpenAI pins as above; `Microsoft.Data.Sqlite`
**10.0.14**. No live endpoint, real credentials, model use or production scaffold.

| Flow | Verified result |
| --- | --- |
| Successful call, persisted reload, continuation | First execution commits the actual tool result and deliberately stops before another model request. A fresh loop/adapter loads only SQLite history and issues request two. Exactly one tool effect; historical call/result not reexecuted. Final assistant text/message ID also committed and reopened. |
| Native fidelity across that reload | Full initial input prefix, appended definition payload/developer role, native `at_1` / `rs_1` / `fc_1` IDs, encrypted reasoning, item order, call/result pairing and decoded counted result survive. Executable registry entries do not duplicate the appended definitions in top-level tools. All six requests retain the tested plan-route flags. |
| Failed / incomplete / clean EOF | Each real adapter projection contains the complete-looking parsed call. The native positive-terminal guard rejects it before the actual loop dispatches: zero effects, one request, no recorded authorized call or claimed result. |
| Recording failure after successful effect | Injected exception after the SQLite result UPDATE but before COMMIT rolls back that transaction. One effect, no second request. Reopen retains the earlier unresolved-call marker, not a result receipt; the continuation loader refuses to run or replay it. Emitting a tool result was not treated as durability. |

**Test-only persistence:** one `test_envelope` row stores whole-history message JSON,
indexed native JSON and a ready/unresolved marker atomically in SQLite, with WAL /
`synchronous=NORMAL`. Connections are non-pooled and closed before reopen. This
reuses the earlier raw-item capture/reconstruction logic, **not** the in-progress
SQLite adapter or its message-row/preview implementation. The marker is deliberately
conservative: it cannot tell whether an external effect happened. This is not a
selected session schema/API, exactly-once guarantee, automatic recovery policy,
process-kill test or power-loss qualification. The execution recreation is explicit
and in-process, not a backend restart. The fixture buffers the provider response
before publishing it; production streaming UX/cancellation remains unqualified.

Evidence root: `%TEMP%\urd-foundation-streams-6268b517\composed-execution-c997bc2e\`.
After acquiring **and starting** `urd:heavy-local-work`, run its `verify.ps1 -LeaseId
'<actual-started-lease>'`; it restores offline from retained read-only caches into
its own cache, then builds/runs sequentially without build servers. `run.log`,
`build.log`, and `runs/20261104-082705-641/` retain the passing transcript, captured
HTTP/SSE and five databases. Final build: zero warnings/errors; pre-build free RAM
555 MiB. Owned process check was empty before releasing the lease. Independent
read-only acceptance review of source, wire captures, logs and these claims is
recorded with the fixture; it is not an independent runtime rerun.

## Control and responsibility seam — proposal, not a new class layout

Current Pie reference inspection found steering at start and after a model/tool
batch (`harness/pi/packages/agent/src/agent-loop.ts:167–268`), not a requirement
to interrupt between each individual tool. Its session-control/state contracts
separate non-waking notes, explicit work, execution end and durable terminal.
Preserve useful behaviour, not Pie's process/reducer machinery.

Session Design owns the integrated structure. This stream proposes only:
- **Shared execution operation:** one straightforward model → validate → tool →
  record → continuation cycle, usable by both primary and bounded sessions.
  Do not introduce per-provider or per-session-kind loop subclasses.
- **Request-only `IChatClient` path:** provider settings, native-item reconstruction
  and truthful terminal/error conversion; auxiliary title/compaction/pruning calls
  can use it without acquiring a tool loop or a full agent session. No extra
  `ModelRequestExecutor` facade is required (owner feedback via Session Design).
- **Session/live-run owner:** accepted work, cancellation, queued steering and
  recording authority; supply that lifetime to the cycle. Browser disconnect
  cancels observation, not work. Provider admission applies per request, not a
  process-wide serial executor.
- **Tool dispatch:** resolve exposed names against current executable/permission
  state, preserve call/result pairing, and distinguish executed, failed, cancelled
  and uncertain outcomes. Exposure is not execution authority.

Session Design's provisional scenarios also require one live run/session, Stop
settling before restart, earlier edits waiting for stop before suffix replacement,
and stale-run publications being rejected. Queued input edits must not indiscriminately
clear notes. These are integration constraints, not all exercised here. Independent
worker closure is not assumed to follow a parent cancellation tree. Restart must
not replay a missing/uncertain result as if its tool never acted.

## Owned-code/dependency observations

Count: physical nonblank, non-`//`-comment C# lines, including braces/usings;
identical formatting convention, shared tests excluded equally. Final independent
review recounted:

| Source | Lines |
| --- | ---: |
| `loop/UrdLoop.cs` | 39 |
| `loop/LibraryPolicy.cs` | 19 |
| Shared `loop/ModelBoundary.cs` | 42 |
| Compared owned subset | **81 Urd vs 61 library** |

The common boundary includes test-specific steering retention, simulated saving,
admission and terminal checks; it is not a proposed production abstraction. Shared
loop fixtures/tests are 261 lines. The adapter fixture is 262 lines including HTTP,
SSE, assertions and file persistence—not a provider-adapter implementation estimate.
Its 15-line save/load helper is **not** the full glue cost: raw capture, role mapping,
terminal checks, result encoding, route restrictions and tests also matter.

The isolated loop assembly set contains seven cached packages: Abstractions + full
MEAI + five supporting packages. Full MEAI therefore adds six beyond Abstractions
in that isolated graph. **Do not call this six incremental application dependencies**:
provider adapters overlap it. The standalone OpenAI adapter fixture resolved 13
packages without full MEAI. No comparative binary size, memory, CPU or production
maintained-LOC measurement was made. Neither prototype contains all features the
library implements; the owner chose a scope/dependency trade-off, not a benchmark winner.

## Reproduce, review and next gate

Temporary evidence root (not a durable source archive):
`%TEMP%\urd-foundation-streams-6268b517\execution\`.

After acquiring and starting exclusive `urd:heavy-local-work`:
```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$env:TEMP\urd-foundation-streams-6268b517\execution\verify.ps1" -LeaseId '<actual-started-lease>'
```
Release only after the script/children exit. It builds/runs the two fixtures
sequentially, restores solely from the retained local package cache, and writes
`loop/{build,run}.log`, `adapter/{restore,build,run}.log` and native wire captures
under `adapter/artifacts/`. Toolchain pins and cached-package locations are in the
projects. Latest build logs are clean; loop summary is **63 passed, 0 failed**;
adapter log ends **ALL PASS**, seven requests.

Independent read-only review caught the original missing failure-shaped-stream
checks; these were added and run before final review. Final reviewer inspected
source/logs, independently recounted LOC and approved the bounded claims; it did
not claim an independent runtime rerun. The execution-stream lead ran the corrected fixtures.
Source SHA-256: loop `Program.cs`
`b021fbd7f30c4b1736693e2a2fe1227c503019c1a3a6bb6506dede0358f7355d`;
adapter `Program.cs`
`8995b14fbbc910c2d2c60c59c294daf1eb301b6b16a65ab201d4e7737759cdf3`.

**Next gate, after the integrated structure is approved:** qualify this composition
against the actual session/record owner, including stale-run and final-record
lifecycle rules. The follow-up above closes the local adapter/loop/SQLite-envelope
seam only; it does not choose or implement that owner. A real-provider round trip
still requires explicit approval; no account action is implicitly authorized here.

Remaining: actual wrapper exposure/model reliability, native provider acceptance,
namespace calls, images, multiple/interleaved calls, network errors/cancellation,
retries/fallback, real durable recovery and noncooperative tools. Claude/Ollama/
Copilot were not adapter-tested here; their current limitations belong in Provider
Access and the earlier research. Cache savings remain unmeasured. No user question
is pending in this stream, and no production work follows automatically.
