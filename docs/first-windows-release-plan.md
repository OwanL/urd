# First Windows release plan

**Private Windows trial.** Once the concurrent work is integrated, qualify the real
Windows distributable on the owner's machines, not a developer checkout or the earlier
[source-packaging fixture](repo-foundations.md#evidence--conventional-pattern-and-bounded-trial).
The trial is private and should exercise the intended release workflows. It does not authorize
publishing, installing into a live profile or using live data for qualification. The browser ships
first; VS Code follows. Desktop, LAN/Tailscale, automatic updates and macOS/Linux feature parity
are outside the first Windows milestone.

The [private trial handoff](private-windows-trial-handoff.md) records the retained candidate,
verification to date, and how to resume on another machine.

## Agreed user flows

- **Start on Windows:** for a portable download, download the Windows ZIP, choose **Extract All…**,
  then double-click `Start.exe` in the extracted folder. If installed via a Windows installer,
  open **Urd** from the Start menu. Either opens the default browser automatically. Starting the
  same copy again opens it instead of starting a second backend. Bundle the .NET runtime so no
  separate .NET install is needed. Reuse Git for Windows if present and handle missing Git as
  part of setup without a separate trip to a download page. Ordinary launch does not fetch
  build tools or the browser automation dependency.
- **Return or stop:** use the tray's **Open Urd** to get back to the same chats or **Stop Urd**
  to stop the backend. Closing the browser or VS Code leaves work running.
- **Open an existing Windows copy in VS Code (follow-up):** tray → **Open in VS Code**. On first
  use, add the Urd sidebar if needed and open it to this copy's chats. After that, the same menu
  item just opens the sidebar. No Marketplace visit and no second Urd install, including when
  Urd was installed through a Windows installer. From the sidebar, reuse the selected copy or
  start it if stopped; if there are multiple copies, let the user choose and remember the
  choice. If Urd itself is missing, the sidebar's **Set up Urd** should get the same standalone
  app. Do not bundle a second backend with the extension or use setup to silently upgrade Urd.
- **Edit and try:** start from a fresh copy in a writable folder such as `My Urd` (extract the
  portable download again). Open its included `source/` directly in an editor. Run **Build**
  (for example, double-click `Build.cmd`), which gets missing pinned build tools and stages the
  result without replacing the running build. Explicitly run the staged build (for example,
  `Run Staged Build.cmd`). The custom copy starts with its own data; later rebuilds preserve
  that copy's data, subject to the supported schema range.
- **Share:** run **Package** (for example, `Package.cmd`) to produce a shareable download with
  the runnable app, the exact source it was built from, and the build recipe. No Git workflow,
  hosted build, official signing key or upload is required. The recipient uses the same start
  flow. Never include chats, settings, sign-ins or caches.

Keep the same logical layout across platforms: runnable app, directly editable source, and
copy-local persistent data kept outside generated and packaged inputs. The runtime and launcher
can differ by OS and CPU; a Windows `.exe` is not a cross-platform distribution format. ZIP is
for the portable Windows trial, not a decision about the only public package. Installer tooling,
the public setup and update flow, and how future clients find and open a selected copy remain open.
Qualify the trial's PortableGit fallback on a clean machine. **Open in VS Code** is an agreed UX,
not a requirement to build or ship the editor client in the first Windows trial.

## Run the portable trial

1. From an integrated source copy on Windows x64, run `Package.cmd`. It puts a ZIP in `builds/`.
2. Extract the ZIP somewhere writable and double-click `Start.exe`. Data belongs to that folder
   under `data/`. Repeat launches reopen that copy. Check `data/server.log` if startup fails.
3. To try changes, edit the extracted `source/` and run `Build.cmd`. Stop that copy from the
   tray, then run `Run Staged Build.cmd` to switch to the staged build. `Start.exe` reopens it
   on subsequent launches. `Package.cmd` builds a new ZIP from a fresh capture of the edits.

The launcher prefers installed Git for Windows; otherwise it attempts to provision pinned
PortableGit in the per-user cache. Build and Package also get missing pinned build tools there.
The initial trial is not a supported public release; clean-machine dependency setup and
performance still need qualification.

## 1. Take a candidate after active work lands

- Collect the active sessions' changes, verification and known issues. Pick one integrated
  revision for the candidate; don't package the shared working tree while other sessions edit it.
- Review first-release scope against core workflows, not Pie parity. Explicitly record which
  gaps block release and which are deferred (for example, Snooze, after-run queue and broader
  recovery). Recheck the subagent cleanup path that untracks a child after unconfirmed process
  cleanup; verify that Stop and shutdown account for owned work even when cleanup fails.
- Propose one initial Windows target (Windows 11 x64 is a starting point). Try the portable
  package with the runtime bundled. Document prerequisites and optional downloads. Verify
  how missing Git is provisioned on a standard account before choosing the delivery method.

## 2. Build the actual source-carrying package

- Build and package from the **same fixed source inputs**. Carry editable backend, UI and
  authored guidance/assets, pinned manifests and locks, build/package instructions and scripts,
  and applicable licenses/notices. Account for third-party distribution obligations. Custom
  packages must carry the edits, additions and deletions they were built from without requiring Git.
- Keep application data, credentials, logs, previous packages and build/cache outputs out of
  the source payload. Keep persisted data outside generated outputs and package inputs. A new
  custom copy starts with separate data; a clean/rebuild of the same copy preserves its data.
- On a clean Windows machine without .NET, Git or build tools, launch the package from a writable
  folder with spaces. Confirm first-run Git setup, automatic browser opening, repeat launch into
  the same copy, tray Open/Stop and clean restart. Repeat on a machine with Git already installed
  and check that it is reused. Use isolated data and a local provider stub for the session/tool
  flow, including a browser close/reopen while work continues. Separately authorized live checks,
  if needed, are not part of routine qualification.
- From a fresh custom copy, make a visible edit. Confirm Build does not replace the running app,
  then run the staged build explicitly. Package it, extract it on another machine, verify that
  behavior and source match, and rebuild it without the original checkout. Confirm that the
  original copy is unchanged, that a rebuild preserves the custom copy's data, and that the
  redistributed copy starts without that data. No offline or bit-for-bit rebuild is promised.

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
- [Schema 36 is the durable-data baseline](../AGENTS.md), including for development builds.
  Qualify upgrades from schema 36 once a newer schema exists, including failed-upgrade recovery.
  Before publishing, separately verify install, uninstall and reinstall preserve the copy's
  chats, attachments, settings and sign-ins on the same machine. Use isolated data.
- Review distribution channel, first-run Windows warnings/signing expectations and the evidence
  with the owner. Publishing the artifact is a separate decision, not a result of passing tests.
- Keep the portability door honest with a follow-up macOS smoke check: build, start without
  platform-specific tools, open the browser, exercise a fake-provider session and shut down.
  This is not a Windows release blocker or a macOS support claim. Record what needs a platform
  implementation before committing to the next OS.
