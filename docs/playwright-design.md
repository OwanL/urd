# Browser automation

Status: design draft; Google sign-in qualified by spike. Replaces the shipped preview (shared profile, restricted tool, headed sign-in
relaunch, network proxy). The earlier design and probes are in Git history before this revision.

## Goals

- The user signs into a site once (including Google and Sign in with Google); the login lasts as
  long as the site allows, across chats and Urd restarts.
- Agents complete multi-site flows on their own, for example: read a policy number from Gmail,
  sign into the insurer, read the emailed verification code, enter it and report the answer.
- When the agent is blocked (password it doesn't have, captcha, MFA prompt), the user takes over
  inside Urd, then hands control back. This works from another device on the LAN.
- Each chat drives its own pages without coordinating with other chats.
- Few lines of code, few tool actions, low memory when idle.

## Trust model

The browser has the same trust as the shell tools: none of the earlier restrictions remain. Agents
read full page text, type anything (including codes and passwords the user supplies), and reach
any destination including localhost. Browsing stays available with LAN hosting on; securing the
network is the user's responsibility. Urd's API already rejects requests from other origins, so a
web page cannot drive Urd. Agent guidance, not code, asks the agent to leave payments, sending mail
and similar final actions to the user.

## One browser, pages per chat

- One persistent Chromium context (`LaunchPersistentContextAsync`) with a `browser-profile`
  directory under the data root. Every chat shares its logins.
- A chat owns the pages it opens and any popup whose opener it owns (OAuth popups follow their
  chat). A chat can only list and drive its own pages. Pages run concurrently; there is no
  browser-wide lock.
- Launch on first use. Close a chat's pages when the chat closes or after 30 idle minutes; logins
  survive because they live in the profile. Exit Chromium after 5 minutes with no pages.
- Limitation: two chats cannot be signed into the same site as different accounts.

## Always headless; the user sees the page in Urd

Chromium never shows a window. To watch or take over, the user opens a viewer in the chat:

- Frames: `page.Screencast.StartAsync(new() { OnFrame = ... })` delivers JPEG frames, sent over a
  per-viewer WebSocket and drawn on a `<canvas>`. Frames flow only while a viewer is open.
- Input: canvas mouse, wheel, key and paste events are scaled to page coordinates and replayed with
  `page.Mouse` and `page.Keyboard` (`InsertTextAsync` for paste).
- The viewer follows the chat's active page and shows its pages as tabs, so an OAuth popup appears
  where the user expects it.
- Because the same page keeps running, a half-finished login, popup or form survives the handoff.
  There is no headed relaunch and no profile release.

A disposable probe already showed frame display and click mapping on headless Chromium with
35–61 ms local click-to-frame latency (Git history). Real identity providers were not tested.

## Handoff

- `handoff` with a short message for the user: the chat shows the message and the viewer with
  **Done**. The tool call waits. **Done** returns a fresh snapshot to the agent; Stop cancels.
- The user can also open the viewer at any time to watch. Typing or clicking in it takes control:
  agent actions on that page wait until the user clicks **Done**.

## Agent tool

One `browser` tool, shaped like Playwright MCP. Every action returns a fresh snapshot of the active
page, capped in size with a truncation note.

| Action | Arguments |
| --- | --- |
| `navigate` | `url`, optional `new_tab` |
| `snapshot` | |
| `click` | `ref` |
| `type` | `ref`, `text`, optional `submit` |
| `press` | `key` |
| `select` | `ref`, `values` |
| `back` | |
| `tabs` | optional `select` or `close` page id |
| `screenshot` | returns an image for vision-capable models |
| `click_at` | `x`, `y` from the last screenshot (captchas, canvas) |
| `wait` | `text` or `ms` |
| `handoff` | `message` |

Snapshots use `page.AriaSnapshotAsync(new() { Mode = "ai" })`, which includes page text, iframes and
`[ref=eN]` references; actions resolve refs with the `aria-ref=` locator. Captchas: the agent may
try with `screenshot` and `click_at`, then hands off if it fails.

The email-code flow needs no special support: the agent opens Gmail in a second tab, searches, reads
the code from the snapshot and types it into the first tab.

## Google sign-in under automation

Google refuses sign-in when it detects an automated browser ("This browser or app may not be
secure"). A spike (2026-11-24, Playwright 1.65.1, headless Edge 158, settings below) passed with the
owner's real account: Google sign-in with 2FA through the screencast viewer, a full browser restart
with Gmail still signed in, and Sign in with Google on a third-party site. The viewer also worked
from another laptop over LAN. The page reported `navigator.webdriver = false` and no `Headless`
marker in the user agent or client-hint brands. Chrome for Testing and phone input were not tested.
A viewer must receive the latest frame when it connects: unchanged pages produce no new frames.

- Launch without `--enable-automation`, with `--disable-blink-features=AutomationControlled`, and
  remove the `Headless` marker from the user agent.
- Prefer a branded browser: Edge is always installed on Windows (`Channel = "msedge"`, no download).
  Fall back to the provisioned Chrome for Testing elsewhere.
- If Google still refuses headless, run headed with an off-screen window; the screencast viewer
  works the same.
- Headless has no browser-owned dialogs: passkeys and Windows Hello are unavailable, so the user
  chooses another sign-in method. The browser's save-password prompt is also unavailable.

## Keeping logins

The profile directory keeps persistent cookies and site storage across restarts. Session cookies
(no expiry) die when Chromium exits, as in a normal browser, which would sign the user out of some
sites after an idle shutdown or Urd restart. On a clean browser exit, save session cookies from
`StorageStateAsync()` beside the profile and add them back with `AddCookiesAsync` on launch. Check
first whether the profile preference "Continue where you left off" achieves this without code.

## Replaces

Delete the network proxy (`PlaywrightNetwork*`), profile lease and serialization, the headed
sign-in relaunch with its routes and UI prompt, the sensitive-field filter, the LAN pause and
`URD_PLAYWRIGHT_LOCAL_ORIGINS`. Keep browser provisioning for the fallback.

## Qualification order

1. Done: spike with real Google sign-in (see above).
2. Browser host and `browser` tool, replacing the current tool.
3. Viewer and handoff in the chat, including from a LAN device.
4. End-to-end mailbox-to-insurer flow, including an emailed code and a user-solved captcha.
