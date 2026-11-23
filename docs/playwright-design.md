# Browser automation

Status: design draft under review; Google sign-in qualified by spike. Replaces the shipped preview (shared profile, restricted tool, headed sign-in
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
- Each session, chat or helper (subagent), owns the pages it opens and any popup whose opener it
  owns (OAuth popups follow their session). A session can only list and drive its own pages. Pages
  run concurrently; there is no browser-wide lock. A helper's pages close when it finishes.
- Agents hand work over by URL: logins are shared, so the receiving session opens the link in its
  own page. Live pages never move between sessions; state that only exists in a page (a
  half-filled form, a used one-time link) means one agent should own the whole flow.
- Launch on first use (about 1–2 s). A session's pages close when the chat is closed or snoozed,
  when a helper finishes, or after 30 minutes without agent or viewer activity, but never while
  one of its questions to the user is pending. Logins survive because they live in the profile.
  Exit Chromium 5 minutes after the last page closes.
- Limitation: two chats cannot be signed into the same site as different accounts. The tool
  description tells agents the profile is shared, so they check which account is active and tell
  the user rather than signing someone out.

## Always headless; the user sees the page in Urd

Chromium never shows a window. To watch or take over, the user opens a viewer in the chat. Working
assumption: an expandable panel whose tab strip lists the chat's and its helpers' pages. Placement
and interaction need their own UI ideation before implementation (open question).

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

- There is no dedicated handoff action. The agent uses `ask_user` to say what the user should do
  ("Solve the captcha, then reply Done"); the question blocks the agent, appears in the chat
  (including questions from helpers) and Stop cancels it. When the asking session has pages, the
  question shows **Open browser**. The reply can carry information back to the agent.
- The viewer is always live and interactive; there is no control mode. If the user acts while the
  agent is working, the agent's next action may fail or see a changed page, and it re-reads the
  page. Stop removes the agent.

## Agent tool

One `browser` tool, shaped like Playwright MCP. As there, every action returns a full snapshot of
the active page, with no browser-specific trimming. Oversized results use the general tool output
cap (prerequisite below).

| Action | Arguments |
| --- | --- |
| `navigate` | `url`, optional `new_tab` |
| `snapshot` | |
| `click` | `ref` |
| `type` | `ref`, `text`, optional `submit` |
| `fill_form` | list of `ref` + `value` (text, checkbox, select); one snapshot for the whole form |
| `hover` | `ref` (real pointer hover; JS cannot trigger CSS `:hover`) |
| `press` | `key` |
| `select` | `ref`, `values` |
| `back` | |
| `tabs` | optional `select` or `close` page id |
| `screenshot` | returns an image for vision-capable models |
| `click_at` | `x`, `y` from the last screenshot (captchas, canvas) |
| `wait` | `text` or `ms` |
| `upload` | `ref`, local file `paths` |
| `dialog` | `accept` or `dismiss`, optional prompt text |
| `evaluate` | JavaScript run in the page; returns its JSON result |

An open dialog (alert, confirm, prompt, leave-page) is reported in the result and waits for
`dialog` rather than being dismissed silently. Results also list new console errors and failed
requests, when there are any, for testing web apps. Drag is omitted as rare; the agent asks the
user instead.

Downloads save to a per-session folder in the OS temp directory, removed when the chat closes;
the agent moves files elsewhere if they should be kept. The action result gives the path. Headless
Chromium downloads PDFs rather than displaying them; reading PDFs belongs in the general `read`
tool, not the browser.

Snapshots use `page.AriaSnapshotAsync(new() { Mode = "ai" })`, which includes page text, iframes and
`[ref=eN]` references; actions resolve refs with the `aria-ref=` locator. Captchas: the agent may
try with `screenshot` and `click_at`, then asks the user if it fails.

The email-code flow needs no special support: the agent opens Gmail in a second tab, searches, reads
the code from the snapshot and types it into the first tab.

## Browser and Google sign-in

Urd uses one browser it owns for all browser work: the Chrome for Testing build pinned by its
Playwright version, already provisioned for web_read. It does not depend on installed Edge or
Chrome, so every machine runs the same version; it updates when Urd updates Playwright.

Google refuses sign-in when it detects an automated browser ("This browser or app may not be
secure"). A spike (2026-11-24, Playwright 1.65.1, settings below) passed with the owner's real
account on both headless Edge 158 and Chrome for Testing 158.0.8127.18, each with a fresh profile:
Google sign-in with 2FA through the screencast viewer, a full browser restart with Gmail still
signed in, and Sign in with Google on a third-party site. The viewer also worked from another laptop
over LAN. Pages reported `navigator.webdriver = false`. Phone input was not tested. A viewer must
receive the latest frame when it connects: unchanged pages produce no new frames.

Measured on the same machine with fresh profiles, Edge, Chrome and Chrome for Testing were alike:
launch in about 0.6–0.7 s, about 250 MB private memory with one blank page, and about 75–150 MB
more per loaded page. Idle shutdown is what keeps resource use low.

- Launch without `--enable-automation`, with `--disable-blink-features=AutomationControlled`, and
  remove the `Headless` marker from the user agent.
- Risk: Chrome for Testing identifies as plain Chromium, and a pinned build ages between Urd
  updates. If Google starts refusing it, revisit using an installed browser.
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
`URD_PLAYWRIGHT_LOCAL_ORIGINS`. Keep browser provisioning. web_read keeps its own browser for now; running it as a
no-login context in the same Chromium is a later follow-up to save memory.

## Prerequisite: one tool output cap

Only bash and read cap their own output today; other tools, including MCP tools, do not. Before the
browser work, apply one size cap with spill-to-file (the bash pattern, 50 KB / 2,000 lines) to every
tool result in the shared tool runner, as a separate change.

## Qualification order

1. Done: spike with real Google sign-in (see above).
2. Browser host and `browser` tool, replacing the current tool.
3. Viewer and handoff in the chat, including from a LAN device.
4. End-to-end mailbox-to-insurer flow, including an emailed code and a user-solved captcha.
