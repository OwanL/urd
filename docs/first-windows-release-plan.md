# First Windows release plan

**Planning only.** Let the current Urd work settle before starting release implementation or
qualification. This plan does not authorize publishing, installing a candidate, running against
live data, or changing the project's supported data policy.

The goal is one browser-first, same-machine Windows release that people can run, inspect, edit
and rebuild. Qualify the actual Urd distributable, not a developer checkout or the earlier
[source-packaging fixture](repo-foundations.md#evidence--conventional-pattern-and-bounded-trial).
Follow [product direction](product-direction.md); no desktop/editor host, LAN/Tailscale, updater,
extension system or macOS/Linux feature parity is needed for this release.

## 1. Take a candidate after active work lands

- Collect the active sessions' changes, verification and known issues. Pick one integrated
  revision for the candidate; don't package the shared working tree while other sessions edit it.
- Review first-release scope against core workflows, not Pie parity. Explicitly record which
  gaps block release and which are deferred (for example, Snooze, after-run queue and broader
  recovery). Recheck the subagent cleanup path that untracks a child after unconfirmed process
  cleanup; verify that Stop and shutdown account for owned work even when cleanup fails.
- Propose one initial Windows target (Windows 11 x64 is a starting point) and document runtime,
  Git for Windows and other prerequisites, including any optional tool downloads. Decide the
  launcher, package format, runtime bundling and data location together. Start by evaluating a
  portable archive and small scripts, not an installer or bootstrap platform by default.

## 2. Build the actual source-carrying package

- Build and package from the **same fixed source inputs**. Carry editable backend, UI and
  authored guidance/assets, pinned manifests and locks, build/package instructions and scripts,
  and applicable licenses/notices. Account for third-party distribution obligations. Custom
  packages must carry the edits, additions and deletions they were built from without requiring Git.
- Keep application data, credentials, logs, previous packages and build/cache outputs out of
  the source payload. Keep persisted data outside generated outputs and package inputs. A new
  custom copy starts with separate data; a clean/rebuild of the same copy preserves its data.
- On a clean Windows environment with only documented prerequisites, launch the package from
  a writable folder (including a path with spaces) and confirm readiness, browser access, tray
  Open/Stop and clean restart. Use isolated data and a fake provider for the session/tool flow,
  including a browser close/reopen while work continues. Separately authorized live checks, if
  needed, are not part of routine qualification.
- From the included source alone, make a visible edit, rebuild and repackage. Extract the custom
  package, verify that the behavior and source match, and rebuild it without the original checkout.
  Confirm that cleaning/rebuilding kept the first copy's existing data and that neither package
  contains it. No offline build or bit-for-bit rebuild promise is implied.

## 3. Measure that same candidate locally

Refresh the schema-bound [transcript diagnostic](browser-development.md#toolchain-and-checks)
against the candidate; keep it a local diagnostic, not CI or a restored browser test suite.
Record the revision, hardware, browser, fixture and measurement method. Use ordinary sessions
and a stress run with long history, large Markdown/tool results and images, repeated tab switches
and an active stream. Distinguish warm views, cold views with derived structure, and first-time
formatting. Report distributions and worst cases, not just medians.

| Check | Acceptance basis |
| --- | --- |
| Input and streaming | Keystroke paint in about one frame (~16 ms); text visible within 100 ms of arriving at the backend. State if the measurement uses a proxy for paint. |
| Navigation and smoothness | Warm tab ≤33 ms; derived-cold tab ≤100 ms; no main-thread task over 50 ms during streaming or scrolling. Content must be usable, not just a painted loading state. |
| Background cost | Verify idle work against the ~30 s heartbeat allowance and check that formatting does not delay model/tool execution under contention. Record browser and backend CPU and memory before, during and after repeated heavy-tab and inspection cycles. Look for memory growth with history length or failure to settle after release. A cache limit is not a process-memory budget. |

Use the [provisional targets](TRANSCRIPT-DESIGN.md#L789-L804)
and report misses or unmeasured targets explicitly. Agree on a practical memory budget from the
results before claiming low overhead; do not silently change a target to make the candidate pass.

## 4. Review the evidence before publishing

- Run the [local verification suite](testing.md#verification) for the candidate and retain a short
  release record: revision, artifact/checksum, supported target, prerequisites, build-and-customize
  result, performance results and known limitations. Recheck affected gates on a rebuilt candidate.
- Before a **supported** public release, the owner explicitly establishes the durable-data
  baseline and reviews the upgrade/data-retention promise. Until then follow the pre-release
  reset policy in [AGENTS.md](../AGENTS.md); don't add pre-release migrations or imply that
  existing dev data is supported. Keep live data out of qualification.
- Review distribution channel, first-run Windows warnings/signing expectations and the evidence
  with the owner. Publishing the artifact is a separate decision, not a result of passing tests.
- Keep the portability door honest with a follow-up macOS smoke check: build, start without
  platform-specific tools, open the browser, exercise a fake-provider session and shut down.
  This is not a Windows release blocker or a macOS support claim. Record what needs a platform
  implementation before committing to the next OS.
