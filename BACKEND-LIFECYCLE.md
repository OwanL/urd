# Backend lifecycle: disposable qualification

**2026-11-04 — Windows experiment, not production bootstrap.** ASP.NET Core/Kestrel
is selected. One backend shared by clients remains a **working default**, not a
locked topology. This report tests local coordination mechanics against
[the lifecycle requirements](README.md#share-the-backend-across-user-interfaces)
and [hosting boundaries](ARCHITECTURE.md#aspnet-core-hosting-and-http); it does not
choose application classes, layout, tray integration or authentication.

## Smallest candidate examined

One cooperating backend candidate per launcher tries to open a persistent,
namespace-local `owner.lock` with `FileShare.None`:

1. **Own:** retain the handle for the backend's entire lifetime. Start Kestrel on
   IPv4 loopback, then publish discovery through a closed sibling staging file
   and same-directory replacement. Discovery contains protocol/scope, random
   instance ID, endpoint, diagnostic PID and a disposable test-only stop key.
2. **Attach:** if ownership is unavailable, read discovery and verify its
   protocol/scope/instance against the loopback endpoint. Wait briefly for a
   starting owner; if identity remains unverified, report unavailable. Never
   steal ownership because a health request failed.
3. **Stop:** verify the instance, then request graceful shutdown with its test
   key. Stop/dispose the server, remove only that instance's discovery, and
   finally release ownership. Never unlink the lock pathname while participants
   can be alive. PID/port hints never authorize process termination.
4. **Recover:** a dead process releases the OS lock. A new owner can replace stale
   discovery after successfully listening. An occupied endpoint reports bind
   failure; it does not authorize killing its occupant.

Discovery readers explicitly allow delete sharing so Windows readers do not
unnecessarily obstruct replacement. Publication is not a power-loss durability
claim. The random identity and stop key are fixture plumbing, **not a selected
local-security or LAN authentication design**; hostile same-user interference is
outside this probe.

## Executed evidence

Fixture: `%TEMP%\urd-foundation-streams-6268b517\backend-lifecycle-237c1caf\`.
It contains `Program.cs`, `BackendLifecycle.csproj`, and `run-{1,2,3}/results.json`;
second/third console logs are retained alongside them. The tiny Web SDK project
uses the installed ASP.NET shared framework, with no external packages.

Windows 11 build **26300**, local **NTFS**, SDK **10.0.403**, .NET/ASP.NET
runtime **10.0.14**. One sequential build
passed with **zero warnings/errors**, compiler/build servers disabled. Three
sequential executions each passed **26 assertions** across the following flows
—not 78 independent scenarios or a performance/stress qualification:

| Flow | Observed result |
| --- | --- |
| Two actual launcher processes released through one barrier; publication delayed | Exactly one owner and one attached result, same instance/endpoint; attached process exits while backend remains reachable. |
| Live owner, discovery deliberately changed to a wrong instance | Another launcher times out unavailable; original owner remains reachable and is not killed. |
| Stale discovery with dead endpoint and a PID belonging to the live test runner | New owner publishes a new instance; stale stop fails; runner survives. **PID reuse is simulated**, not forced OS recycling. |
| Stale record pointing at an unrelated fixture's occupied port and live PID | Explicit-port start fails without publishing success or stopping that listener. A separate ephemeral-port attempt succeeds while the unrelated listener survives. |
| Explicit verified stop | Owner exits successfully; discovery removed; inert lock file retained. |
| Forced exit of our retained child process after publication | Stale discovery remains; subsequent launcher acquires released ownership and publishes a new instance. Old discovery cannot stop the replacement. |

Only the test harness forcibly terminates processes, using retained `Process`
objects for children it created, never a PID read from discovery. All children
are awaited and disposed, including failure cleanup. A final Windows process
query found **no fixture processes remaining**. Heavy-work lease was released
after cleanup. Source/results remain in temp for review; no service registration,
startup entry, real data path, firewall change, personal setting or live backend
was used. Repository change is this report only. Independent read-only acceptance
review inspected the source, all three result files and this report; no actionable
issue was found. It did not independently rerun the fixture.

Re-run on Windows from that fixture directory:

```text
dotnet build BackendLifecycle.csproj --disable-build-servers --maxcpucount:1 -p:UseSharedCompilation=false
dotnet bin/Debug/net10.0/BackendLifecycle.dll suite <fresh-owned-temp-run-directory>
```

## Portability and remaining limits

- **Windows runtime evidence only.** No macOS/Linux execution, LAN/Tailscale,
  installer, service/tray, detached desktop/VS Code launcher or real browser was
  exercised. The owner candidate remains a child of the test harness; exit of
  the *attached* launcher was tested, not packaged client/daemon separation.
- .NET 10 baseline Unix [source](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/Microsoft/Win32/SafeHandles/SafeFileHandle.Unix.cs)
  maps `FileShare.None` to advisory `flock`, can disable locking through
  `DOTNET_SYSTEM_IO_DISABLEFILELOCKING`, and ignores some unsupported-lock errors.
  Therefore successful file opening is **not universal proof of exclusion**.
  Actual target runtimes/local filesystems must pass this contention/crash suite;
  this probe does not qualify network filesystems or silently choose a portable
  production lock implementation.
- Same-directory replacement avoids the cross-filesystem copy fallback in
  [.NET file moves](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/IO/FileSystem.Unix.cs).
  Namespace/lock-file replacement while live is excluded; identity checks are
  ordinary cooperative discovery, not protection against hostile local code.
- No production shutdown/drain policy, hung-owner recovery, restart of agent
  work, stop/start race suite, crash during publication, high-volume contention,
  source-build activation or authentication was qualified. Discovery success is
  a point-in-time observation, not a promise the backend stays alive.

**Recommendation:** retain this as a small mechanics candidate, not a generic
lock/coordinator framework. Before production use, repeat on actual macOS/Linux
and connect it to the eventual launcher and shutdown lifecycle. No consequential
owner choice was needed for this disposable probe, and no owner question is
pending. Existing tray, startup-at-login, shutdown UX, access/authentication,
production structure and topology decisions remain open; this experiment does
not resolve them.
