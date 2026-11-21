# Playwright browser automation

Status: enabled by default on Windows. The native tool, shared profile and chat handoff are implemented and locally tested with synthetic sessions. Twin Sandbox and real identity providers have not been qualified.

## Try it locally

Start Urd normally. Playwright is available in primary chats, and Chromium is launched on demand. If Chromium is not cached, Urd provisions it on first use.

Public sites need no setup. For local development, give the host the exact origins to allow, for example from PowerShell:

```powershell
$env:URD_PLAYWRIGHT_LOCAL_ORIGINS = 'http://localhost:3000'
.\Start.exe
```

Supply comma-separated origins if needed. Use the exact scheme, hostname, and port registered as the callback origin with your identity provider. Playwright pauses while unauthenticated LAN hosting is active. If a browser task is still running, finish or stop it before enabling LAN hosting.

## One browser profile

All local Urd chats use one Urd-owned Chromium profile. There is no profile picker, project binding, or per-chat permission prompt. Signing in once makes that site's browser session available to later chats, including after a Urd restart, until the site requires sign-in again. This is a separate profile, not the user's everyday Chrome profile. The `playwright-profile` directory lives under Urd's durable local data root, outside the Chromium install cache. Chromium manages saved credentials, persistent cookies and other site data on disk; Urd does not export tokens or copy passwords into its database. To start with a blank browser, stop Urd and rename that directory before relaunching it.

The shared profile is shared account access, not a read-only grant. Make that clear on the first sign-in. Authenticated browser automation is local-only in v1; defer running it on hosts serving unauthenticated LAN clients. No per-site integrations. Leave [web search and reading](web-access-plan.md) separate, with their existing public, read-only behavior unchanged.

## Browser and chat handoff

- Primary chats get a native `playwright` tool using Urd's existing .NET Playwright dependency and bundled full Chromium. It supports navigation, a bounded accessibility summary, click, fill, and sign-in handoff. It does not support screenshots or arbitrary scripts in this preview. Use the same executable and `LaunchPersistentContextAsync` profile path in both modes. Launch on demand. Share login state, not live tabs: serialize browser ownership in v1 so another chat or subagent cannot operate on a tab or take over the profile during a handoff.
- The agent can request a sign-in handoff when it encounters an auth gate. Urd shows **Sign in** in the requesting chat. The button reopens the last explicit navigation destination, not a model-supplied sign-in link or an in-flight OAuth redirect. The user should verify the browser address before entering credentials. Show the destination origin in chat; don't expose transient OAuth URLs or their query parameters.
- On **Sign in**, close the headless context, wait for it to release the profile, and open the visible browser. Block browser-tool actions, page observations and captures while the user is in control. The user completes the site's redirects and MFA before clicking **Continue**. Close the visible window before reopening headless with the same profile and returning to the task. Resume from a safe URL and inspect the resulting page; don't blindly replay a click or submit. The live tab, its in-memory state, and partially completed login transactions are not moved between processes.
- The typed handoff follows Urd's pending-question pattern: a session update triggers a fresh read, and the pending handoff is kept in memory. A chat reload can recover it while the host is alive; Stop, a closed sign-in window, and shutdown must end the handoff and release the browser. Do not resume automation automatically if the sign-in window is closed without **Continue**.
- Allow public sites and explicitly targeted local dev origins, including the hosted login redirects that the user needs for those sites. Keep browser sandboxing and an enforceable destination boundary; do not give pages blanket access to localhost, private services, the Urd control plane, or `file:` URLs. Do not expose browser settings or password manager pages through the tool, or offer arbitrary Playwright scripts in v1. Website content is untrusted; a generic browser cannot infer that every click is safe. For sending mail or buying something, leave the final action to the user rather than claiming the tool can reliably detect every side effect.

## Verification and limits

- Lifecycle and prompt tests cover exclusive ownership, explicit Continue, cancellation, and closing the visible window. Opt-in loopback tests (`URD_TEST_PLAYWRIGHT_PROFILE=1`, `URD_TEST_PLAYWRIGHT_HANDOFF=1`, pinned browser already cached) cover persisted login across launches, server-side session revocation, and the visible-to-headless handoff with the production proxy. The browser tests use disposable profiles; no database migration is needed for the single profile.
- Plain `ws://` connections and chunked HTTP uploads are not supported by the proxy, so some dev tooling, including Vite hot reload, may not work. HTTPS stays end-to-end encrypted. The browser is not a read-only account boundary. Page summaries may still contain account data, and tool results follow Urd's usual model and transcript handling.
- Next qualification: drive the complete chat flow against Twin Sandbox with opt-in, including a real API-backed action after restart. Also verify the browser's native save-password prompt. Google account password sync is not a preview requirement.

## Local feasibility evidence

Pinned .NET Playwright 1.65.0 and the bundled full Chrome for Testing 158.0.8127.18 passed a loopback test: visible sign-in, headless restart with the same profile, server-side session revocation, visible re-sign-in, and another successful headless run. A fresh profile was unauthenticated in a separate fixture. With fake credentials added via the built-in Password Manager, the bundled browser and installed Chrome both autofilled after relaunch. The native save prompt and Google Password Manager sync from the user's everyday profile were not verified. No live site, account, or personal profile was used.
