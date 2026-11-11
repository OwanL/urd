# History compaction — design decisions

**Status: implemented 2026-11-11; verification recorded below.**

Compaction summarizes older conversation so a chat fits the model's context window. It is
distinct from tool-result pruning and catalog pruning (ARCHITECTURE.md, "Context management").

## Decisions

1. **Copy Pi initially; keep it simple and light.** Pi's pinned `docs/compaction.md` is the
   reference:
   - automatic when context tokens exceed `contextWindow - 16384`;
   - keep roughly the last 20,000 tokens verbatim, summarize the older part;
   - cut at turn boundaries, never between a tool call and its result; a single oversized turn
     is split as in Pi;
   - structured summary (goal, constraints, progress, decisions, next steps, critical context,
     files read/modified), each new summary building on the previous one;
   - when a provider rejects a request as too long, compact and retry that request;
   - a manual "compact now" with optional focus instructions.
2. **Models carry their context limit.** Model metadata gains a context-window value, as with
   model family on saved model settings. Source order: provider-reported where the API supplies
   it, else a built-in table seeded from Pi's model registry (exact known model IDs on known
   services only); user-editable. An unknown limit still allows Send; compaction then happens
   only on overflow or on request. The configured limit stays fixed. Headroom is the smaller
   of 16,384 tokens and one fifth of the window; recent history is at most 20,000 tokens,
   reduced to half the threshold for small windows. Summary output is capped at 8,192 tokens
   or one quarter of a known window, whichever is smaller.
3. **Existing Urd requirements still hold:**
   - original history stays readable (session-design.md, ARCHITECTURE.md);
   - a transcript marker shows where compaction took effect, with the summary inspectable
     (TRANSCRIPT-DESIGN.md decision 22);
   - failed or cancelled compaction leaves usable context and sets no boundary;
   - compaction never replays tools;
   - the summary call uses the shared request-only `IChatClient`.
4. **Hook:** context preparation before each model request (agent-tools-design.md).

## Execution and storage

`core/compaction/` owns budgeting, safe-cut planning and request-only summarization.
`session_compactions` stores only successful summaries and their first retained request/message
position, scoped to the session's history revision. A metadata-only transcript marker commits
with the summary; expanding it reads the bounded summary separately. Original messages, tools,
images, usage and execution identities are never rewritten. Context reads use the latest
summary plus its retained suffix, dropping provider-native payloads from the summarized span.

Threshold preparation runs before each model request. A classified `context_length_exceeded`
may cause one compact-and-retry, only when the rejected exchange produced no output or calls.
Previously executed tools are not retried. Generic HTTP failures and message text do not trigger
this recovery. Reported usage anchors the next threshold check; later history is estimated
with a four-characters-per-token fallback, not a tokenizer guarantee.

The manual compaction HTTP operation is idle-only and accepts optional focus instructions.
It is a separate, addressable execution: Stop cancels and joins its summary request, and reopening
never replays it. The workspace does not expose it as a control; successful automatic compaction
appears as a **History compacted** transcript entry with an inspectable summary.
Busy Send cannot steer into manual compaction. Failure, cancellation and stale boundaries do
not replace the previous summary. Schema 16 requires a fresh disposable database or an explicit
owner-approved reset of older data; no automatic migration or deletion is performed.

## Verification

- Isolated Release build and full backend suite: **374/374 passed**. Focused compaction,
  model-window, schema and starting-context checks: **39/39 passed**.
- Coverage includes safe tool boundaries, threshold and overflow preparation, no retry after
  even briefly buffered output, manual idempotency, cancellation cleanup, failed summaries,
  readable original history and reopening with the saved summary boundary.
- UI format/lint/type checks, **278 unit tests**, the real-server HTTP integration and the
  production bundle passed on the integrated tree. Independent UI review approved the idle-only
  control and final-receipt feedback.
- Native browser acceptance on the combined startup-enabled offline preview verified a neutral
  empty-history no-op, successful manual compaction, a transcript marker and its lazily expanded
  saved summary. No browser page errors were reported. This used a disposable local provider,
  not a live account.
- Physical-line budget passed: **22,852 test/helper lines ≤ 28,896 application lines**.
- Independent backend review approved after the buffered-output retry guard was corrected.
  These are offline checks, not a new live-provider qualification. No existing database was
  reset and no running user host was restarted for this verification.
