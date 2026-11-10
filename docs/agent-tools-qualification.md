# Agent-tool process and result qualification

Evidence for the [agent-tool plan](agent-tools-design.md), separating integrated source
qualification from earlier disposable probes. No live model requests, personal databases,
installations or desktop control. Builds/runs used the exclusive `urd:dotnet` resource.

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

**Still pending:** integrated background completion/waiting/recovery, live-provider acceptance,
rich tool-result inspection and compaction. Foreground qualification is not normal-use readiness.
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

### Integration still required

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

Actual storage/context tests, the transcript's approved large-payload work and integrated
Stop/restart/shutdown tests remain delivery obligations, not reasons to redesign the approved
tools or class shapes.
