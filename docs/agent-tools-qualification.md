# Agent-tool process and result qualification

Evidence for the [agent-tool plan](agent-tools-design.md), separating live acceptance,
integrated source qualification and earlier disposable probes. Each checkpoint states its
boundary. No personal databases, installations or desktop control were used. .NET checks
used `urd:dotnet`; the final acceptance also reserved `urd:heavy-local-work`.

## Live tools and host recovery acceptance — 2026-11-11

The frozen tool slice passed bounded real-provider checks using a **fresh, separately
authenticated disposable profile**. ChatGPT subscription Responses, `gpt-6.1-sol`, low
reasoning, and explicit `openAI`/image-support metadata were selected; capabilities were
not inferred from the model name. **15 model requests** were made, without automatic
retries. This request count is evidence, not a spending cap.

| Check | Observed result |
| --- | --- |
| Foreground tools | `write` created a file; `edit` changed its exact value; `read` returned it. One emitted batch then exercised text read, `bash`, and image read. All five calls succeeded. |
| Live image delivery | A generated 320×240 PNG became a persisted image result. The model correctly identified its blue background and yellow circle; those facts were not supplied in the prompt. |
| Background waiting/wake | A background shell remained visible in `list_pending_calls`; after waiting, its completion automatically woke the execution. The model returned the random token from its result. One completion identity was published and claimed once. |
| Explicit cancellation | `cancel_pending_calls` requested cancellation of a second chat's background shell. Its saved outcome was cancelled; the post-sleep effect did not occur. Its completion was delivered once. The concurrent successful background chat was unaffected. |
| Addressed Stop | Stop during a real-provider-authorized foreground shell killed its known native PowerShell child and settled cancelled. The partial effect remained. Only the original model request existed: no continuation or replay. |
| Save/reopen | Restarting only the owned idle host preserved all four settlements, frozen selections, 11 tool rows, completion identities and a SHA-256-verified image. The 14 existing model requests were unchanged. An explicit new follow-up made request 15, successfully reused saved conversation context, and ran no tools. |

Live checks used the frozen `671112b` tool artifact at
`source/hosting/server/bin/ToolRelevanceAlways-4d11e9d/Release/Urd.Server.dll`, with schema 14.
They do **not** qualify activation of the concurrent schema-15 starting-context changes.
The current shared source separately passed **343 offline backend tests**, including the
new `ToolHostCrashTests` cases; both cases passed an additional focused repeat against
matching isolated Release artifacts:

- The real CLI uses an owned loopback scripted Chat Completions endpoint, starts the actual
  `bash` tool and retains an OS handle to its native sleeping child.
- The Stop case waits for saved cancellation and child exit. The crash case kills **only
  the server root**, not the process tree, so fixture cleanup cannot mask job ownership.
- Restarting that crashed disposable profile records interrupted tool/run failure. The
  effect marker remains single and provider-call count remains one: recovery never replays.
- Independent review approved the test and its minimal shared helpers. The shared-tree
  physical-line check passed: **25,504 test/helper lines ≤ 27,148 application lines**.

Evidence is under `%TEMP%/urd-tools-acceptance-20261111-4d11e9d/`: case JSON records,
`reopen-report.json`, scripts and `cleanup-report.json`. The fresh grant's remote revocation
was confirmed, local credentials cleared, its database profile deleted, both owned host
processes stopped and their ports closed. Neither pre-existing profile was changed by
these checks. No source/API changes or live-server activation were needed.

**Remaining boundaries:** this qualifies the selected ChatGPT Responses route, not other
models/providers or live Chat Completions compatibility. Rich tool-result inspection and
compaction remain follow-on work; passing this slice is not whole-application readiness.

## Integrated background checkpoint — 2026-11-11

Background dispatch, list/cancel controls, waiting/wake, Stop/failure drain and explicit host
recovery are implemented. Verification of the shared tree passed:

- Isolated Release build/analyzers: zero warnings/errors; **322 backend tests** passed.
- UI formatting, typed lint and typecheck; **246 unit tests** (including `sessions/*.test.ts`),
  real-server/SDK integration, **14 headless browser regressions** and Vite production build.
- Deterministic lifecycle tests cover waiting, steering, unchanged in-flight context, one
  completion delivery, held cleanup on Stop/provider failure and prompt cancellation/fencing
  after a background result-save fault. Focused ownership cases passed two additional repeats.
- Storage tests cover atomic result/event commit, exact repeats, late background authorization,
  safe transcript publication, recipient ordering and release of never-attempted claims.
  Explicit recovery distinguishes unstarted/unknown work, preserves actual results and event
  identities, closes interrupted requests/inputs/runs and performs no tool/model replay.
- Offline real-SDK captures for **Responses and Chat Completions** verify stable placeholders,
  separately labelled tool-origin completions, actual image parts, text-only omission and
  reopen fidelity. These are serialization checks, not live-provider acceptance.
- Independent source reviews approved registry, storage/context and final driver integration.
  Physical-line budget: **23,486 tests/helpers ≤ 23,623 application lines**, including the
  concurrently committed Markdown work; no compressed formatting or moved helpers.

Outputs remain isolated at `bin/Tools-4d11e9d/Release/net10.0`; all tests used owned temporary
profiles. Verification children stopped and both resource leases were released. The live
schema-12 server/data were untouched. **Schema 14 is built, not activated**; it requires
owner-directed data recreation, not a migration or an automatic reset.

**At this checkpoint:** live-provider acceptance and an integrated crash-process probe were
still pending; the later acceptance above covers them. Rich tool-result inspection and
compaction remain separate normal-use gates.

## Integrated foreground checkpoint — 2026-11-11

Generic read/write/edit/bash, parallel dispatch, explicit model metadata and processed-image
storage/context are implemented. Local verification passed:

- Release solution build and analyzers: zero warnings/errors; **292 backend tests** passed.
- UI formatting, typed lint and typecheck; **222 unit tests**, real-server/SDK integration and
  **14 headless browser regressions** passed. Vite production build passed.
- Real Git Bash tests exercised supplied environment, Unicode/quoting, nonzero exits,
  inherited-pipe survivors, timeout/cancellation isolation, chat shutdown and output faults.
  Earlier crash evidence remains the disposable fixture below, not an integrated crash test.
- File/queue tests exercised emitted-order admission, queued cancellation, exact edits,
  untouched mixed endings/BOM, strict UTF-8 and bounded paging. The symbolic-link subcase was
  unavailable on this host because Windows link privilege is absent; lexical/case/drive aliases ran.
- Image tests exercised orientation/resize, BMP conversion, exact inline-size boundaries,
  JPEG fallback and retained originals. Offline **Responses and Chat Completions** SDK captures
  verified actual PNG image parts, result ordering, save/reopen fidelity and text-only omission.
- Session/storage tests exercised parallel siblings, failed start/result saves, cleanup-failure
  fencing, Stop returning cancelled results without claiming rollback, and saved-folder reuse
  when host defaults change. Independent source reviews approved the bounded implementation.

Verification used `bin/Tools-4d11e9d/Release/net10.0` outputs and owned temporary data. The
running development server and its schema-12 data were not restarted or reset. Schema 13
requires deliberate data recreation before activation; browser state is version 3.

At this foreground checkpoint, background completion/waiting/recovery was still pending;
the later checkpoint above qualifies it locally. Foreground qualification alone was not
normal-use readiness.
The following **2026-11-10 probes** establish only their stated fixture boundaries; independent
review checked their source/captures but did not independently rerun them.

## Windows process ownership

**The nested-job approach passed the tested lifecycle scenarios.** On this Windows 11 host,
Git for Windows Bash 5.3.15 and .NET SDK 10.0.403 exercised one kill-on-close job per chat and
one nested kill-on-close job per call. Shells were created suspended, assigned to both jobs,
then resumed. Completed calls retained their nested job handles under the chat owner; no
process migration between jobs was needed.

| Scenario | Observed evidence |
| --- | --- |
| Shell exits with a surviving child holding the output pipe | Shell-handle wait completed in 304 ms; nested job still contained one active process; child remained alive after parent-side reader disposal. |
| Cancel a running call | Nested-job accounting changed from four active processes to zero before cleanup was reported; the observed child was dead. |
| Cancel a later sibling | Its job drained to zero while all three earlier survivors stayed alive. |
| End the chat | Terminating the outer chat job alone drained three active processes to zero and killed survivors, before nested-job fallback cleanup. |
| Abrupt fixture-backend death | Self-termination without cleanup closed job handles; an external check found the known survivor gone three seconds later, before its natural sleep deadline. |
| Nonzero shell exit | Exit code 3 did not imply descendants had stopped; the child remained alive. |
| Spaces, Unicode and output | Working directory `sp ace ☃dir` and UTF-8 `☃-hello` output round-tripped. |

### Implementation consequences

- Wait for the **shell process handle**, not output-pipe EOF, to complete an ordinary bash
  call. In a separate control, `.NET Process.WaitForExitAsync` with asynchronous redirected
  output remained pending after the shell had exited because a child held the pipe open.
- Shell exit is **not** proof of tree cleanup. After cancellation/termination, verify the
  call job has zero active processes before reporting cleanup complete. The fixture queried
  `JOBOBJECT_BASIC_ACCOUNTING_INFORMATION.ActiveProcesses` with a bounded wait.
- Assign jobs before resuming the shell. Assigning after `Process.Start` has a structural
  ownership race; the fixture exercised suspended creation, not a reproduction of that race.
- Retaining a completed call's nested job handle provides the survivor handoff. Reclaim empty
  jobs rather than retaining a handle for every historical call indefinitely. The fixture is
  not approval of a new background sweeper or production class layout.

**Limits:** fixture ownership, not integrated `BashTool`, Stop or server shutdown. Custom
per-call environments, timeout configuration, concurrent launch handle inheritance and
non-Windows platforms remain unqualified. Crash verification observed a known child, not
an exhaustive post-crash tree inventory. Reader disposal was tested, not disposal of every
retained call handle while survivors lived. Timing is illustrative, not a performance target.

Evidence root: `%TEMP%\urd-procgate-10614\` (`REPORT.md`, `fixture/Program.cs`,
`logs/main-run.log`, `logs/crash-run.log`, `logs/trap-run.log`). The final fixture is 619 source
lines, including diagnostic modes and P/Invoke; it is not a production LOC estimate. The
follow-up replaced an initial structural-only pass with actual job-accounting checks and
isolated outer-job termination from nested fallback cleanup.

Final source SHA-256:
`7c77f8b537e7feca1fb8057b5febe8da2ab470ad6344fe9d46a5818fda318a49`.

## Background completion representation

The provider fixture uses SDK 10.0.403, Microsoft.Extensions.AI.OpenAI 10.11.0, OpenAI 2.14.0
and Microsoft.Data.Sqlite 10.0.14. It links Urd's checked-in provider files and sends actual
adapter requests to a loopback-only scripted Responses endpoint. Its own SQLite schema
stores the call, unchanged placeholder, separate completion event and image bytes; this is
**not** an end-to-end test of Urd's storage or scheduler.

### Final probe: 43 assertions passed

- The fixture reloads the completion as `background_completion`/`background_tool`, then maps
  it to `ChatRole.User` at the provider edge. Urd's linked provider client replayed its
  labelled text while retaining the original three input items unchanged and exactly one
  function-result placeholder. The original human-authored event remained separate.
- The pinned adapter serialized the reloaded envelope plus a genuine `input_image`; its data
  decoded to the persisted PNG. Origin, call identity, outcome and untrusted-result labelling
  survived the capture. This was adapter serialization evidence, not live provider acceptance.
- At that probe checkpoint, Urd's context guard rejected image-bearing content before HTTP, including
  the user-channel completion. The image capture bypassed that guard deliberately; the test
  does **not** establish integrated Urd image delivery.
- Negative controls retained the developer-role image rejection/omission: bypassing Urd's
  guard did not make the pinned adapter send an image in that role.

The final build had zero warnings/errors. The initial probe had 34 assertions and treated
provider user-role input only as a diagnostic; the final run qualifies the owner-approved
labelled completion mapping, not merely an unrelated user image.

**Owner resolution:** provider wire role does not define authorship in Urd. A completion
remains a tool-origin event, not a human submission, even when its envelope and images use
the provider's ordinary input channel. Keep origin, call identity, outcome and untrusted
result labelling explicit in model context and show the separate tool row in the transcript.
Do not use higher-authority developer instructions merely to avoid a `user` wire role.
The initial probe's interpretation of that role as necessarily human-authored was too strict;
it is not a product constraint. A label is not provider-enforced trust isolation.

### Integration requirements recorded at the probe checkpoint

- Persist and claim completion events separately from the stable direct placeholder; test
  delivery races, restart and Stop against the actual session/storage owners.
- Relax `ToolStorage.Complete`'s newer-request fence only for authorized pending background
  calls; preserve run, session, revision and settlement checks.
- Extend image representation through Urd's context validation/storage and provider edge.
  Preserve image bytes as image parts, not base64 prose, and honor model image capability.
- Qualify the selected route's live acceptance separately with explicit authorization.
  No Chat Completions or other-provider compatibility follows from these captures.

Evidence root: `%TEMP%\urd-background-tool-wire-3c3dc9c7-3ba0-403c-9d92-efe6bbb291e5\`.
`REPORT.md` contains reproduction commands; `run.log`, `build.log`, fixture source and
`artifacts/request-*.json` retain the captures. Both evidence roots are disposable OS-temp
locations, not durable source archives.

## High-level plan alignment

A separate read-only review checked the product direction, architecture, application,
session/execution/discovery, compaction and transcript plans. No material direction conflict
was found. These boundaries still apply:

- [Product direction](product-direction.md): a small Urd-owned harness, Windows first, no
  new general process/scheduler framework and no Pi runtime dependency. Probe size is not
  permission to copy diagnostic scaffolding into production.
- [Session ownership](session-design.md): backend work is independent of visible tabs;
  hiding a tab is not closing a chat. Stop joins run-owned work; completed-call servers remain
  chat-owned until final close/backend exit. Interrupted effects are never replayed.
- [Transcript requirements](TRANSCRIPT-DESIGN.md#on-demand-details-and-images): inspect the
  historical model-facing result, not a separate raw process-output archive. Tool images
  load on demand; compact rows must not eagerly decode them. Preserve managed-reference
  lifetimes rather than adopting the probe's image-BLOB schema as a product decision.
- [Compaction sequencing](compaction-design.md): foreground tools, then background delivery
  and recovery, then compaction. Tool-row presentation and compaction remain normal-use gates;
  passing these two probes does not justify self-hosting early.

The integrated checkpoints above cover the selected tool storage/context, Stop, recovery and
shutdown boundaries. The transcript's approved large-payload work and compaction remain
separate delivery obligations, not reasons to redesign the approved tools or class shapes.
