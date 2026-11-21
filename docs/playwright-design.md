# Playwright browser automation

Status: the internal profile lifecycle is implemented and locally tested. The model-facing tool, network boundary and chat handoff are not wired.

## One browser profile

All local Urd chats use one Urd-owned Chromium profile. There is no profile picker, project binding, or per-chat permission prompt. Signing in once makes that site's browser session available to later chats, including after a Urd restart, until the site requires sign-in again. This is a separate profile, not the user's everyday Chrome profile. Store it under Urd's durable local data root, outside the Chromium install cache. Chromium manages saved credentials, persistent cookies and other site data on disk; Urd does not export tokens or copy passwords into its database. Provide one explicit way to clear the automation browser's data.

The shared profile is shared account access, not a read-only grant. Make that clear on the first sign-in. Authenticated browser automation is local-only in v1; defer running it on hosts serving unauthenticated LAN clients. No per-site integrations. Leave [web search and reading](web-access-plan.md) separate, with their existing public, read-only behavior unchanged.

## Browser and chat handoff

- Add a native `playwright` tool using Urd's existing .NET Playwright dependency and bundled full Chromium. Start with navigation, page inspection, click, and fill. Use the same executable and `LaunchPersistentContextAsync` profile path in both modes. Launch on demand. Share login state, not live tabs: serialize browser ownership in v1 so another chat or subagent cannot operate on a tab or take over the profile during a handoff.
- The agent can request a sign-in handoff when it encounters an auth gate. Urd shows **Sign in** in the requesting chat. The button uses a validated destination from the browser, not a model-supplied credential prompt or a link to the default browser. Show the destination origin in chat; don't expose transient OAuth URLs or their query parameters.
- On **Sign in**, close the headless context, wait for it to release the profile, and open the visible browser. Block browser-tool actions, page observations and captures while the user is in control. The user completes the site's redirects and MFA before clicking **Continue**. Close the visible window before reopening headless with the same profile and returning to the task. Resume from a safe URL and inspect the resulting page; don't blindly replay a click or submit. The live tab, its in-memory state, and partially completed login transactions are not moved between processes.
- Reuse Urd's pending-question lifecycle for waiting, cancellation, and reload recovery, with a typed handoff for the browser actions. Keep the pending handoff in memory. A chat reload can recover it while the host is alive; Stop, a closed sign-in window, and shutdown must end the handoff and release the browser. Do not resume automation automatically if the sign-in window is closed without **Continue**.
- Allow public sites and explicitly targeted local dev origins, including the hosted login redirects that the user needs for those sites. Keep browser sandboxing and an enforceable destination boundary; do not give pages blanket access to localhost, private services, the Urd control plane, or `file:` URLs. Do not expose browser settings or password manager pages through the tool, or offer arbitrary Playwright scripts in v1. Website content is untrusted; a generic browser cannot infer that every click is safe. For sending mail or buying something, leave the final action to the user rather than claiming the tool can reliably detect every side effect.

## First implementation slice

1. The shared persistent-browser owner is covered by lifecycle unit tests and an opt-in, headless loopback test (`URD_TEST_PLAYWRIGHT_PROFILE=1` with the pinned browser already cached). These cover ownership, handoff cancellation, profile reopen and session revocation with a fake site. No database migration is needed for the single profile.
2. Wire the tool and the in-chat sign-in controls. With a local fixture, sign in, continue the original task, use the login from a second chat, restart Urd and use it again, then revoke the fixture session and repeat the handoff. Verify that another chat cannot drive the browser during sign-in, and that cancel or window close leaves no stuck lease or extra browser process.
3. Qualify Twin Sandbox separately with opt-in. Use the stable local callback origin and verify a real API-backed operation after restart. Qualify the browser's native save-password prompt separately; Google account password sync is not a v1 requirement.

## Local feasibility evidence

Pinned .NET Playwright 1.65.0 and the bundled full Chrome for Testing 158.0.8127.18 passed a loopback test: visible sign-in, headless restart with the same profile, server-side session revocation, visible re-sign-in, and another successful headless run. A fresh profile was unauthenticated in a separate fixture. With fake credentials added via the built-in Password Manager, the bundled browser and installed Chrome both autofilled after relaunch. The native save prompt and Google Password Manager sync from the user's everyday profile were not verified. No live site, account, or personal profile was used.
