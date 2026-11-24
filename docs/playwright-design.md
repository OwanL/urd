# Browser automation

Status: browser host, session tool and viewer implemented. Google sign-in passed in the spike;
the finished app is not release-qualified until the real-account checks below pass. Replaces the shipped preview (shared profile, restricted tool, headed sign-in
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
web page cannot drive Urd. Urd's shared agent guidance (all tools, not just the browser) asks
agents to confirm high-risk actions with the user first, such as payments or communicating with
third parties, and to treat page and email content as data rather than instructions. That
guidance does not exist yet; add it in `SharedAgentGuidance.cs` as part of this work.

## One browser, pages per chat

- One persistent Chromium context (`LaunchPersistentContextAsync`) in the existing
  `playwright-profile` directory under the data root, so logins made with the preview survive.
  Every chat shares its logins.
- Each session, chat or helper (subagent), owns the pages it opens and any popup whose opener it
  owns (OAuth popups follow their session). A session can only list and drive its own pages. Pages
  run concurrently; there is no browser-wide lock. A helper's pages close when it finishes.
- Each session has at most 3 pages. Opening another, including a popup, closes that session's
  least recently used page (by agent or viewer), so an OAuth popup does not close the page that
  opened it. The tool description states this, and the result names the closed page.
- Agents hand work over by URL: logins are shared, so the receiving session opens the link in its
  own page. Live pages never move between sessions; state that only exists in a page (a
  half-filled form, a used one-time link) means one agent should own the whole flow.
- Launch on first use (under 1 s measured). A session's pages close when the chat is closed or snoozed,
  when a helper finishes, or after 30 minutes without agent or viewer activity, but never while
  one of its questions to the user is pending. Logins survive because they live in the profile.
  Exit Chromium 5 minutes after the last page closes.
- Limitation: two chats cannot be signed into the same site as different accounts. The tool
  description tells agents the profile is shared, so they check which account is active and tell
  the user rather than signing someone out.

## Always headless; the user sees the page in Urd

Chromium never shows a window. To watch or take over, the user opens a browser window in the chat.
Its tab strip lists the chat's and its helpers' pages.

- Desktop layout: the window works like a small desktop window. It can dock as a split view beside
  the chat (default, with a draggable divider), float (move and resize over the chat), maximize to
  fill Urd, or minimize to a chip. Floating keeps the chat and question cards usable; docked never
  covers them. Rejected: inline in the transcript (scrolls away) and overlay-only (hides the
  question being answered).
- Each chat has its own window. Switching chat tabs hides it, and each chat remembers whether its
  window was open, docked, floating or maximized, plus its size and position.
- The page resizes to match the window while a viewer is open (minimum width about 360px), so text is
  drawn at full size and phones get the site's mobile layout. When two viewers watch the same chat,
  the most recently opened or resized one sets the size. When the last viewer closes, the page
  returns to 1280×800, so unattended work uses desktop layouts. The agent targets elements by ref, so
  a resize cannot misdirect clicks; a ref hidden by the new layout fails and the agent re-reads the
  page. `click_at` is rejected if the page size changed since the screenshot it is based on.
- Opening: when a chat has pages, a "Browser · N tabs" chip appears in the composer's indicator row;
  it opens the window, and a minimized window returns to it. The question card's **Open browser**
  opens the same window. The window never opens by itself.
- Controls are minimal: the window is for watching and handoff, not general browsing. The top bar
  only switches between the chat's and its helpers' tabs; there is no address bar, back/forward,
  reload, new-tab or close button. Navigation is the agent's job.
- Phones: the window is full screen only and can be minimized back to the chat. Safe defaults: tap
  clicks, drag scrolls, a keyboard button opens the phone keyboard and sends text, and the pending
  question stays visible. Phone input is best-effort and untested; refine after real use.
- Tab selection: the window opens on the agent's active page; after that the user switches tabs
  independently, and the user's choice never changes the agent's active page. New pages appear in
  the tab strip without stealing the view, except a popup opened by the user's own input in the
  window, which is shown like in a normal browser.
- Defaults (revisit after use): the window shows no agent status of its own (the chat already does)
  and does not highlight what the agent clicks. When all of a chat's pages close, its window closes
  and the chip disappears.
- Known limitation: frames are rendered at 1 device pixel per CSS pixel (Playwright fixes the scale
  factor per context), so text is softer on high-DPI screens and phones. Refine later if needed.
- Debounce window resizes before resizing the page.

- Frames: Chromium's page screencast delivers JPEG frames, sent over a
  per-viewer WebSocket (input travels back on the same socket, keeping its order) and drawn on a
  `<canvas>`. Frames flow only while a viewer is open. Send the latest frame when a viewer connects.
- Input: canvas mouse, wheel, key and paste events are scaled to page coordinates and replayed with
  `page.Mouse` and `page.Keyboard` (`InsertTextAsync` for paste).
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

One `browser` tool (replacing `playwright`), shaped like Playwright MCP. As there, every action returns a full snapshot of
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
| `click_at` | `x`, `y` from the last screenshot (e.g. canvas controls) |
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
`[ref=eN]` references; actions resolve refs with the `aria-ref=` locator. The agent hands a captcha
or access challenge to the user in the viewer.

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

- Qualification difference: the spike changed how the automated browser identified itself. The
  implementation currently uses standard Playwright identification. The spike's successful result
  is not evidence that this configuration passes; a refusal in the finished app blocks release.
- Risk: Chrome for Testing identifies as plain Chromium, and a pinned build ages between Urd
  updates. If Google starts refusing it, revisit using an installed browser.
- If Google still refuses headless, run headed with an off-screen window; the screencast viewer
  works the same.
- Headless has no browser-owned dialogs: passkeys and Windows Hello are unavailable, so the user
  chooses another sign-in method. The browser's save-password prompt is also unavailable.

## Keeping logins

The profile directory keeps persistent cookies and site storage across restarts. Session cookies
(no expiry) die when Chromium exits, so idle shutdown and Urd restarts would sign the user out of
some sites at seemingly random times. Urd's browser should behave like one that never closes: on
every browser exit, save session cookies (`ctx.cookies()` with no expiry) beside the profile and
add them back with `AddCookiesAsync` on launch. Tested with Chrome for Testing: this restores
them, while the "Continue where you left off" preference does not. Sites still end sessions
server-side when they choose. If Urd crashes, cookies since the last save are lost; accepted.

## Replaces

Delete the network proxy (`PlaywrightNetwork*`), profile lease and serialization, the headed
sign-in relaunch with its routes and UI prompt, the sensitive-field filter, the LAN pause and
`URD_PLAYWRIGHT_LOCAL_ORIGINS`. Keep browser provisioning. web_read keeps its own browser for now; running it as a
no-login context in the same Chromium is a later follow-up to save memory.

## Prerequisite: one tool output cap

Implemented in the shared tool runner: keep at most 50 KiB of UTF-8 text / 2,000 lines inline,
including the notice pointing to the complete temp file. This applies to text returned with images,
errors and remote MCP calls. Do not truncate the source text in the MCP adapter.

Existing pieces to unify rather than duplicate: `tools/runtime/ToolOutputLimits.cs` (the limits),
`tools/shell/BashOutput.cs` (spill file and `fullOutputPath`), web_read's `spillPath`, and
`tools/pruning/ToolResultPruner.HasSpill`, which special-cases both names. Text is capped; image
results are not. The browser tool's own 12,000-character truncation goes away with it.

## Qualification order

1. Done: the spike verified Google sign-in with the proposed browser configuration (see above).
   Release blocker: repeat that flow in the finished Urd viewer using a fresh test profile. The
   owner must be able to sign in with 2FA, restart Urd and reopen Gmail without signing in again,
   and use Sign in with Google on a third-party site. A failed check blocks the browser release;
   Google is not an optional provider.
2. Done: shared tool output cap (separate change).
3. Browser host and `browser` tool, replacing the current tool.
4. Viewer and handoff in the chat, including from a LAN device. Suggested slices: screencast and
   input over WebSocket in a docked window; chip, tabs and **Open browser**; page resizing;
   floating, maximize and minimize; phone full screen.
5. End-to-end mailbox-to-insurer flow, including an emailed code and a user-solved captcha.
