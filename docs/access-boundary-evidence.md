# Access and onboarding: boundary evidence

**2026-11-04 · Decision preparation, not an access design or implementation approval.**
README/ARCHITECTURE remain authoritative. Browser-first, a single-user shared
backend, direct backend-local provider credentials, continuing background work,
LAN/Tailscale, desktop/VS Code and Windows → macOS → Linux remain the destination.
This report owns only a small access/onboarding investigation; it does not select
an authentication framework, transport, desktop host, credential store or topology.

## Morning recommendation

**Recommend a same-machine, same-origin browser first milestone, with explicit
client authority on reads, commands and streams, and provider onboarding on the
backend machine.** This reduces the first qualification surface without removing
remote access. It is a recommendation for owner alignment, **not a selected scope**.
The strongest alternative is remote access in that milestone, accepting additional
connection trust, HTTPS/proxy and device-onboarding work before normal use.

Three findings matter before choosing mechanics:

- **Loopback, an allowed Host, CORS and provider sign-in do not authenticate a Urd
  client.** A browser's inability to read a response is not proof a command failed.
- **Cookies work with native EventSource, but localhost ports do not isolate
  cookies.** The experiment sent a Strict/HttpOnly cookie to a second loopback
  port. Header-based fetch avoids that automatic cookie attachment, but native
  EventSource cannot set its Authorization header; fetch-stream handling is needed.
- **Remote use and remote provider onboarding are different promises.** The
  documented ChatGPT callback returns to the browser device's `127.0.0.1`, not
  automatically to the backend. A backend already signed in is a different case.

## Separate the boundaries

| Boundary | Establishes | Does not establish |
| --- | --- | --- |
| Listen address / firewall / tunnel | Which interfaces and peers can reach a listener. Explicit loopback binding excludes direct LAN listeners. | Which local process/browser may read data or cause effects. A port is not a credential. |
| Host validation | Request addresses an expected backend name. Kestrel binding alone does not validate Host [1]. | Identity or command permission; native HTTP clients can supply Host. No DNS-rebinding test was run. |
| Origin / CORS / browser policy | Origin is scheme + host + port. Origin checks can reject foreign browser requests; CORS governs cross-origin response access/preflight. CSP, mixed-content and local-network permissions add browser restrictions [2,3]. | Authentication. A simple request may reach and mutate a server without CORS approval. Native clients are not bound by browser policy and can supply Origin. |
| Urd client authority | An explicitly issued/accepted client credential or deliberately trusted host/proxy identity can authorize backend access. | Provider entitlement, or automatic permission for every operation. Reads and stream contents need protection too. Bootstrap, lifetime and revocation remain open. |
| Command admission | Backend checks the current authority and addressed operation before accepting effects. Tools act on the **backend machine**, even from a phone/editor. | A new multi-user/RBAC system, or permission derived from a cached UI/tool description. This fixture has only one inert increment, not Urd's execution policy. |
| Provider credentials | Backend authenticates to a particular model provider/route with its granted scopes. | **Client authentication to Urd.** No provider token belongs in client auth headers, browser storage, source builds or the client stream. No Urd account/broker is required. |

Local-network browser restrictions vary by browser/version. Chrome documents
permission-gated public/local → more-private requests and secure-context conditions
[3]. Our two loopback origins do not exercise those transitions. Do not design
around disabling browser protections, add permissive CORS to mask a failure, or
present this local test as mobile/LAN compatibility.

## Surface / access / onboarding matrix

| Surface | Access boundary and useful small shape | Onboarding and remaining qualification |
| --- | --- | --- |
| **Same-machine browser** | Serve UI and API from one backend origin to avoid routine CORS configuration. Still authenticate private reads, mutations and streams; reject unexpected Host/Origin. Browser navigation to localhost is not itself authority. | Needs an explicit local bootstrap into client authority; this experiment **injected** synthetic credentials rather than solving it. Same-machine provider browser/callback is the documented baseline. Browser/profile reload, credential lifetime and multiple local services remain engineering work. |
| **Remote browser: LAN** | Browser connects to the backend's reachable address, not its own localhost. Co-hosting UI/API can remain same-origin. Require a deliberate network/TLS and client-trust boundary; opening a port or accepting its Origin does not supply one. | Pairing/credential delivery, trusted certificate setup and loss/revocation of a client need a usable flow. Plain LAN HTTP does not protect bearer/cookie confidentiality. Same-machine provider onboarding may precede remote use; remote provider onboarding is separately unresolved. No LAN listener or second device was tested. |
| **Remote browser: Tailscale** | Tailnet access is not automatically Urd command authority. An explicit app credential is one option; a restricted, trusted identity-aware proxy is another. Serve documents HTTPS, access rules and identity headers [4]. This is not a Urd hosted identity service. | If trusting Serve headers, prevent direct untrusted bypass/header forgery and decide which single-user identity is accepted; membership of a shared tailnet is not enough by implication. Tagged/shared-device nuances matter. Provider loopback caveat remains. No Tailscale install/configuration/request was used. |
| **Desktop** | Same UI components may load the backend origin, or use a host-specific origin/bridge. A webview does not magically inherit browser cookies or backend authority. Native HTTP bypasses CORS, so the backend still enforces authority. | Host must acquire/transfer client authority deliberately. Virtual-host mapping supplies an origin, not the backend's identity; bridge messages/navigation require validation [5]. Connect/start and UI-close/background requirements stand; desktop host and credential integration remain unselected and untested. |
| **VS Code** | Webview CSP/resource rules and the extension message bridge constrain reuse. Either a qualified direct connection or narrow extension-mediated access is possible; neither authorizes commands by itself [6]. | Local/remote extension host, browser webview and Urd backend can be on different machines. Port mapping is connectivity, not sign-in or permission; documented mapping excludes WebSockets. Remote-SSH/container forwarding is not proof the provider callback works. No extension or host adapter was built. |

### Direct provider sign-in is not remote-client sign-in

OpenAI's current OSS/local contract requires an already-listening
`http://127.0.0.1:<port>/<fixed-path>` callback, exact per-attempt redirect reuse,
PKCE/state/nonce validation and protected local token persistence; `localhost` is
not interchangeable [7]. Thus opening that authorization URL on a phone would
return to the phone's loopback. Do not replace it with a LAN redirect, relay
provider tokens through the UI, or assume a device-code alternative exists for
this route. A separately documented/qualified forwarding or other flow would be
new evidence, not something this test establishes.

[provider-qualification.md](provider-qualification.md) already covers candidate
offline OAuth/refresh glue and its limits. Authentication, model entitlement and
inference remain separate. Copilot's supported standalone route is still a gap;
its device OAuth documentation is not qualification of a ChatGPT flow or neutral
Copilot model transport. Ollama/API-key alternatives do not erase subscription
scope. No live provider authentication/inference call, account operation or actual
credential-store access occurred here.

## Protected credentials and background availability

Closing all UIs while the same user remains logged in is **not** equivalent to
running before login, after logout, or as an OS service. README's background
requirement does not select those latter environments. The store must be available
to the actual backend identity without borrowing an open UI's process lifetime.

| OS candidate facility | Documented constraint; consequence, not a selected store |
| --- | --- |
| **Windows DPAPI / Credential Manager** | DPAPI normally binds decryption to matching user credentials, usually the same computer; machine scope lets other users on that machine decrypt [8]. User-profile loading matters during impersonation [9]. Credential Manager uses the current token's logon-session credential set; network logons have none [10]. A user background process and a differently identified service are not interchangeable. These docs do not say a visible desktop is always required. |
| **macOS Keychain** | Keychains may lock; an item-access operation can require user approval, and denial/allow-once differs from persistent permission [11]. A logged-in background backend is not guaranteed prompt-free secret access. Qualify the chosen app identity, locked/unlocked state and unattended failure path on macOS; no Keychain operation was performed. |
| **Linux Secret Service** | The specification describes a service in the user's login session, protocol sessions tied to a D-Bus session-bus connection, locked secrets and prompt-mediated unlocking [12]. Availability/unlock state cannot be assumed in a no-session service or every distribution. The published document is an API draft, not a distro/runtime qualification. |

Engineering follow-up: test locked/unavailable stores without losing saved
credentials, atomic replacement and refresh identity/races, then recover through
explicit user attention rather than a plaintext fallback or hidden billing route.
This is not a new service manager or a requirement to stop all background work
when one account needs attention. Startup-at-login/pre-login operation and offscreen
attention UX retain their existing open status.

## Tiny executable qualification

Owned temporary fixture:
`C:\Users\OwanLazic\AppData\Local\Temp\urd-foundation-streams-6268b517\access-boundary-6e87019b\`.
Only this report is committed. `Program.cs` is 94 lines: two explicitly IPv4-loopback
Kestrel listeners, one protected in-memory counter, a separate deliberately unsafe
control counter, a read and a one-event SSE response. No chat, history, SQLite,
agent, real tool, login service, auth library or production class scaffold.

- Windows build **26300**, .NET SDK **10.0.403**, runtime **10.0.14**; framework-only
  Web SDK project, no external packages. Build passed with **0 warnings/errors**,
  build servers/shared compilation disabled. These are experiment versions, not pins.
- Headless Chromium **151.0.7922.34**, two fresh isolated browser contexts using the
  harness's existing browser binary; no user profile, screenshots or visible control.
  Same-site foreign origin was a second owned `127.0.0.1` port, **not** another device.
- Random per-run synthetic header/cookie values; no persistent client registration.
  Cookie installed through the test API with `HttpOnly`, `SameSite=Strict`, host-only
  scope, `/` path and **no Secure flag on this HTTP-only fixture**. This does not
  qualify issuance, HTTPS cookies or production secret protection.
- Explicit fixture checks enforce exact Host/optional supplied Origin and separate
  cookie/header credentials. Origin absence alone never grants authority. These
  are deliberately visible test predicates, not ASP.NET authentication qualification.

| Check | Observed evidence |
| --- | --- |
| Missing/wrong authority on read, increment and stream, both credential routes | Raw matrix returned **401**; fresh browser fetches without authority also returned 401. Rejected mutations did not change the protected counter. |
| Foreign Host, foreign Origin and `Origin: null`, with valid synthetic authority | Raw HTTP matrix returned **400 / 403 / 403** on all three operations and both routes. Raw requests are not browser evidence; a browser cannot freely set Host. Valid header authority with absent Origin could read, demonstrating Origin is not identity. |
| Same-origin header authority | Read and POST returned **200**; counter became 1. Fetch's body reader received the expected `text/event-stream` event. |
| Native EventSource with an attempted Authorization option | Browser emitted an error; server recorded **401 and no bearer header**. Native EventSource exposes `withCredentials`, not arbitrary headers [13]. Header-bearing fetch succeeded instead. |
| Same-origin cookie authority | Read/POST and native EventSource succeeded; counter became 2. HttpOnly cookie was absent from `document.cookie`. |
| Foreign-origin cookie read, POST, no-cors POST and credentialed EventSource | Server received the **Strict cookie**, recorded the foreign Origin and **403** in all four cases. Browser saw fetch errors, an opaque status-0 result or EventSource error—not the status detail. Protected counter remained **2**. |
| Foreign-origin Authorization POST | Server recorded **OPTIONS 403**, with no subsequent POST. This is a real browser preflight result, distinct from native/raw Origin tests. |
| No-CORS-only negative control | Foreign `mode:'no-cors'` simple POST returned an opaque browser result but the deliberately unprotected server counter became **1**. A browser failure/opaque response alone does not prove command rejection. |
| Cookie port-isolation negative control | The second-port server received the valid cookie on its own path. Cookie scope has no port boundary [14]. HttpOnly prevents JS reads, not browser delivery to another same-host port; Strict is same-site, not same-origin. |

**45 + 21 assertions passed in one sequential two-context run**, including raw
matrix checks—not 66 independent security scenarios. Server audit records and
final counters accompany browser observations, so a blocked response cannot be
mistaken for a rejected effect. EventSources were closed after their first outcome;
all waits had bounds. No performance, sustained streaming, reconnect, logout,
mid-stream revocation, DNS rebinding, cross-site-cookie or hostile-local-process
resistance claim follows. An Origin filter cannot repair a stolen credential.

Evidence: `header-probe.js`, `cookie-probe.js`, corresponding `*-results.json`,
`server-evidence.json`, `build.log`, `server.log`, `server-error.log`, `ready.json`,
`AccessProbe.csproj`, `global.json`, `Program.cs` and `RUN.md`. Probe sources contain
only now-inert synthetic test values. Build and browser work held
`urd:heavy-local-work`; browser runtime closed, owned server exited cleanly, and a
process check found no fixture process before releasing it. No lifecycle/Stop/
receipt probe was rerun. Independent method/scope review approved the bounded
plan before execution. Final read-only review independently recounted **66 passing
assertions / 53 server audits**, checked source, controls and report, and found no
actionable issue. It did not rerun the browser/build or independently verify live
cleanup or external docs; `review.md` retains that boundary.

## Choices, costs and next gates — no decision inferred

| Candidate | Benefit | Cost / qualification still needed |
| --- | --- | --- |
| **Same-origin browser + HttpOnly cookie + native EventSource** | Conventional browser session mechanics; JS need not read the credential; native SSE manages connection mechanics. | Explicit bootstrap and cookie lifetime; CSRF/origin policy; cookie host/port sharing is real, not solved by Strict. HTTPS/Secure deployment and host-specific cookie jars need qualification. Do not promise isolation from other same-host services. |
| **Same-origin browser + explicit client token + header fetch/stream** | Authority is deliberately attached to intended requests rather than ambient host cookies; usable by native clients too. | Secure bootstrap/token handling; client JS holds authority. Fetch stream framing/reconnect/error handling needs implementation or focused reuse. This probe only read one event; it did not build that client layer. Tokens in stream URLs are not a proposed workaround. |
| **Remote access in the first milestone** | Earlier cross-device usefulness and earlier discovery of deployment issues. Co-hosted UI/API still avoids unnecessary CORS. | Add qualified TLS/network/proxy trust, client enrollment/revocation and real-device/browser checks. Same-machine provider onboarding can still be a stated limitation. Neither Tailscale nor SignalR supplies app authorization by implication. |

**Owner-level choice:** whether same-machine access/onboarding is sufficient for
the first usable milestone, or remote use must arrive with it. If remote onboarding
or unattended pre-login operation is important immediately, that changes the scope
and cost; neither is silently promised or ruled out here. Bring any meaningful
trust/remembered-client UX trade-off with a concrete flow, not an auth-framework vote.

**Engineering choices:** exact headers/endpoints, standard auth-library reuse,
CSRF/CSP checks, stream parsing, platform store APIs and tests. Do not require the
owner to design these or add tenancy/RBAC infrastructure. Conditional recommendation:
prefer explicit header/fetch mechanics if avoiding ambient localhost cookie sharing
is part of the required boundary; prefer cookie/native EventSource only with that
cost acknowledged and the intended host/bootstrap flow qualified. Neither becomes
selected because its toy test passed.

Before normal use: qualify real client bootstrap and credential lifetime; reject
unauthorized reads/commands/streams through the chosen production path; test the
actual credential store/backend identity and provider onboarding separately. Later
remote/desktop/editor tests must use those real surfaces and preserve backend-local
execution. Prior [browser](BROWSER-DESIGN.md), [backend lifecycle](backend-lifecycle.md)
and [provider](provider-qualification.md) evidence does not close these gates.

## Official sources

Public documentation inspected 2026-11-04; mutable documentation is not a runtime
support guarantee. OS/host constraints also received a focused independent
read-only source review; no OS credential stores were accessed.

1. [Kestrel Host filtering](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/host-filtering?view=aspnetcore-10.0).
2. [ASP.NET Core CORS](https://learn.microsoft.com/en-us/aspnet/core/security/cors?view=aspnetcore-10.0).
3. [Chrome 142 LNA release note](https://developer.chrome.com/release-notes/142#local-network-access-restrictions), [current LNA guidance](https://developer.chrome.com/docs/modern-web-guidance/guides/security/local-network-access).
4. [Tailscale Serve: HTTPS, access rules, identity headers and bypass caveat](https://tailscale.com/kb/1312/serve). No Serve/Funnel configuration was changed; Funnel is not a proposed Urd path.
5. [WebView2 local content/origins](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/working-with-local-content#loading-local-content-by-using-virtual-host-name-mapping), [host security guidance](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/security). Example constraints, not a host selection.
6. [VS Code webviews](https://code.visualstudio.com/api/extension-guides/webview#security), [API declaration: WebviewPortMapping](https://github.com/microsoft/vscode/blob/main/src/vscode-dts/vscode.d.ts).
7. [OpenAI OSS/local registration and sign-in](https://developers.openai.com/siwc/token-sharing-open-source/sign-in).
8. [Windows CryptProtectData](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata).
9. [.NET ProtectedData: profile/impersonation remarks](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.protecteddata?view=windowsdesktop-9.0).
10. [Windows CredReadW: logon-session credential sets](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credreadw).
11. [Apple Keychain locking](https://support.apple.com/guide/keychain-access/lock-and-unlock-your-keychain-kyca1242/mac), [access permission](https://support.apple.com/guide/keychain-access/if-youre-asked-for-access-to-your-keychain-kyca1243/mac).
12. [Secret Service introduction](https://specifications.freedesktop.org/secret-service-spec/latest/ch01.html), [sessions](https://specifications.freedesktop.org/secret-service-spec/latest/sessions.html), [locking](https://specifications.freedesktop.org/secret-service-spec/latest/unlocking.html), [prompts](https://specifications.freedesktop.org/secret-service-spec/latest/prompts.html).
13. [HTML standard: EventSource interface](https://html.spec.whatwg.org/multipage/server-sent-events.html#the-eventsource-interface).
14. [RFC 6265 §8.5: cookie confidentiality / no port isolation](https://www.rfc-editor.org/rfc/rfc6265#section-8.5).
