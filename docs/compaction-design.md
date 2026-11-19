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
   - a manual "Compact now" action. The HTTP operation accepts optional focus instructions;
     the browser control does not expose that field.
2. **Models carry their context limit.** Model metadata gains a context-window value, as with
   model family on saved model settings. Source order: provider-reported where the API supplies
   it, else a built-in table seeded from Pi's model registry (exact known model IDs on known
   services only); user-editable. An unknown limit still allows Send; compaction then happens
   only on overflow or on request. The configured limit stays fixed. Headroom is the smaller
   of 16,384 tokens and one fifth of the window; recent history is at most 20,000 tokens,
   reduced to half the threshold for small windows. The summary request caps output at 8,192
   tokens or one quarter of a known window, whichever is smaller. The ChatGPT route does not
   accept an output cap; the same local completion, size and context-reduction checks still apply.
3. **Existing Urd requirements still hold:**
   - original history stays readable (session-design.md, ARCHITECTURE.md);
   - a transcript marker shows where compaction took effect, with the summary inspectable
     (TRANSCRIPT-DESIGN.md decision 22);
   - failed or cancelled compaction leaves usable context and sets no boundary;
   - compaction never replays tools;
   - the summary call uses the shared request-only `IChatClient`.
4. **Hook:** context preparation before each model request (agent-tools-design.md).
5. **Background-call inputs survive compaction.** Append a harness-generated list containing
   the tool name, request ID, call ID and full stored arguments. Snapshot it before summary
   generation; a tool finishing during compaction must not lose its input. Include running
   calls, completed calls awaiting delivery, and calls whose completion stays in retained
   history. Rebuild the list from storage on each compaction, including calls whose original
   message was removed by an earlier compaction. Inputs are not summarized or truncated;
   they bypass the generated-summary output limit but count toward estimated context size.
   This does not change completion delivery or replay any tool.

## Execution and storage

`source/backend/harness/compaction/` owns budgeting, safe-cut planning and request-only summarization.
`ModelHistory` contains model-visible messages and the previous saved summary, not transcript rows.
`HistoryMessageLocation` orders those messages by execution ordinal, request index within the execution,
and message index within the request; the request ID locates stored content but does not sort it.
`HistoryCompactionPlan` identifies the prefix to summarize and the first message to retain.
Planning takes the existing `StartingContext` so instructions and tool declarations stay together.
`HistoryCompactionBoundary` prefers whole turns, then assistant continuations.
`ContextTokenBudget` names the estimation allowances and limits.
`CompactionTriggerTokenCount` is an optional application preference, read once at each compaction check.
Automatic uses the model window minus fixed headroom (20% of the window, capped at 16,384 tokens).
A configured positive count triggers above that count, capped at the same window-minus-headroom limit;
it also enables automatic compaction when the model window is unknown. Without either a count or a
known window, only manual/overflow compaction is available. The retention budget is the smaller of
20,000 tokens and half the effective trigger; without any trigger it remains 20,000. The summary is
separate from this retention budget. If no safe split fits the budget, compaction can keep
an over-budget latest turn intact and still summarize older history, including on an automatic trigger.
Saved preferences apply to the next check in existing sessions; an already prepared compaction
plan does not change midway.
Edit summarization instructions in `CompactionPrompt`; its renderer keeps native payloads/reasoning
out of the prompt and bounds tool-result text. `ManualCompactionRequest` starts an idle-only execution;
a `CompactionReceipt` acknowledges that execution, not necessarily a saved summary.
Summary and transcript metadata expose `estimatedContextTokensBefore/After`: estimated total context
usage including instructions and tools, not the model's window capacity. Stored SQL columns are unchanged.

`session_compactions` stores only successful summaries and their first retained request/message
position, scoped to the session's history revision. A metadata-only transcript marker commits
with the summary; expanding it reads the bounded summary separately. Original messages, tools,
images, usage and execution identities are never rewritten. Context reads use the latest
summary plus its retained suffix, dropping provider-native payloads from the summarized span.
The background-call appendix is saved with the summary in the existing text field; no schema
change or database reset is needed. The summarizer is instructed not to copy the previous
appendix, since the harness supplies a fresh list after the generated text.

Threshold preparation runs before each model request. A classified `context_length_exceeded`
may cause one compact-and-retry, only when the rejected exchange produced no output or calls.
Previously executed tools are not retried. Generic HTTP failures and message text do not trigger
this recovery. If there is no safe cut, the overflow error explains why retry was skipped. A failed
summary is not a rejected chat request and does not trigger another compaction. Failure feedback
retains local summary-validation details and the provider's bounded, redacted error when available.
Reported usage anchors the next threshold check; later history is estimated
with a four-characters-per-token fallback, not a tokenizer guarantee.

The manual compaction HTTP operation accepts optional focus instructions; the browser's
**Compact now** action does not expose that field. The action is in the interactive Context
popover, also reachable from More when context indicators overflow. It is enabled only when the
session is truly idle—including after background tools and cleanup have finished—and a model is
selected. It leaves composer drafts unchanged and does not automatically repeat a no-op or failure.
Manual compaction is a separate, addressable execution: Stop cancels and joins its summary request,
and reopening never replays it. Busy Send cannot steer into manual compaction. Successful manual
or automatic compaction appears as a **History compacted** transcript entry with an inspectable
summary. No-op and failure feedback do not add a boundary; failure, cancellation and stale
boundaries do not replace the previous summary. The pre-release schema policy requires a fresh
disposable database or an explicit owner-approved reset of incompatible data; no automatic
migration or deletion is performed. See [backend development](backend-development.md) for the
current schema.

## Verification

### Background-call appendix — 2026-11-15

Isolated Release build and **541 backend tests** passed, including **52 focused compaction
and background-storage cases**. Coverage includes full inputs larger than the summary limit,
repeated compaction after the original call leaves context, completion during summarization,
pending recipients, removal after completion is summarized, and appendix token accounting.
Independent review passed. No running host was replaced or database reset.

### Original compaction checkpoint

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
