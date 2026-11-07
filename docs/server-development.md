# Local server development

The first hosting slice is a runnable **health-only** ASP.NET Core server. It composes
Urd's existing Core and SQLite; it is not a second backend. Chat HTTP routes, UI,
static assets, startup recovery and real providers are not implemented yet.

## Run

Use the pinned SDK and locked restore described in [backend development](backend-development.md).
From `source/`, with `$dotnet` set to that SDK's executable:

```powershell
& $dotnet build Urd.slnx --no-restore --configuration Release --disable-build-servers -m:1 -p:UseSharedCompilation=false
& $dotnet .\hosting\server\bin\Release\net10.0\Urd.Server.dll --data-root 'C:\dev\repos\urd\data' --port 0
```

Choose your own absolute data directory. Both arguments are required; either order is
accepted. Port `0` lets the OS select an available port; an explicit port must be within
`1..65535`. Unknown, duplicate or malformed options fail before opening data.
There is no bootstrap, wrapper script or automatic browser launch in this slice.

After SQLite opens and Kestrel binds, stdout contains exactly one readiness record,
for example:

```json
{"event":"urd-ready","origin":"http://127.0.0.1:56123"}
```

Use the actual printed origin, not this example or a guessed port. Diagnostics go to
stderr. Invalid arguments exit with code `2`; startup/run failures exit with code `1`.
Ctrl+C requests graceful shutdown. The run returns successfully only after its HTTP
and Core shutdown operations both finish; it has no forced cleanup timeout.

## HTTP and local access

- The only endpoint is `GET /health` or `HEAD /health`: **204**, without a body or private
  state. Requests with a body are rejected with 400. Other methods receive 405;
  unknown paths, including `/api/...`, receive 404 after admission checks.
- The listener is explicitly IPv4 `127.0.0.1`. Default appsettings, environment and URL
  configuration providers are not loaded; they cannot add listeners or change the data root.
- Every request requires a loopback peer and Host `127.0.0.1` with the actual served port.
  Omitting the port is valid only for HTTP's default port 80. Forwarded headers are ignored.
- Origin may be absent for public health requests. If supplied, it must be exactly the
  canonical served origin; foreign, `null`, multiple and noncanonical origins receive 403.
  There are no CORS grants, credentials or cookies. API-specific admission remains a later bite.
- Before readiness and once shutdown admission closes, the gate returns 503. These checks
  restrict ordinary foreign browser access, not trusted native/local programs.

## Data and lifetime

`Program.RunServerAsync(ServerOptions, Action<Uri>, CancellationToken)` owns one run.
`ServerOptions` validates paths/ports without I/O. The run creates the explicit directory,
opens `urd.lock` with exclusive sharing, then opens `urd.sqlite` and composes one store,
manager and deterministic streaming `FakeChatClient`. Constructors do not start model work.
The fake client returns `Hello from Urd.`; no health request invokes it.

A second Urd host for that directory fails before opening SQLite. The lock file is retained;
its open handle, not its contents or a PID, conveys ownership. Separate data roots can run
concurrently. This cooperative lease does not prevent another native program from directly
opening SQLite. Never delete a live data directory or its lock file to bypass ownership.

On stop, admission closes first. Core session shutdown and HTTP stop/drain are initiated
and both are awaited without the caller's cancellation token or the host's default timeout.
Only a successful drain permits application/store/client disposal and lease release.
Startup or readiness-callback failures also enter cleanup. A drain failure is reported and
the CLI exits unsuccessfully without explicitly disposing resources beneath uncertain work.
The stop token requests shutdown; it is not a deadline or permission to abandon cleanup.

Normal reopen preserves saved history and does not replay work. Opening a database does
**not** recover orphan runs or make a previously fenced conversation usable again.

## Verification and review

Locked restore and the Release solution suite pass **184/184** tests with no compiler or
analyzer warnings. The 72 hosting cases cover options/admission, actual Kestrel requests,
two clients/data roots, startup failure, orderly stop/reopen and preservation of stored
sessions, receipts, requests and output. Child-process tests cover CLI readiness, paths
with spaces, hostile ambient listener configuration and competing-process ownership.
In an isolated copy, those child tests also rejected deliberate regressions that enabled
ambient listener configuration or shared the directory lease; restored sources passed again.

The child readiness test explicitly terminates its own health-only process after inspection;
that is **not** evidence of graceful shutdown. Separate in-process tests use the actual
server's stop token and await completion. Active model work during HTTP shutdown remains
unqualified until command routes exist; existing Core tests independently cover session
drain, callbacks and persistence failures. No browser, load or real-provider claim follows.

Review `source/hosting/server/Program.cs` first, then `ServerOptions.cs`,
`LocalBrowserGate.cs` and `FakeChatClient.cs`. Tests are in
`source/tests/Urd.Backend.Tests/hosting/`, using `support/RunningServer.cs`,
`support/ServerProcess.cs` and the existing SQLite evidence readers.
The [hosting design record](scratch/initial-ui-hosting-design.md) separates this approval
from future transport/UI work.
