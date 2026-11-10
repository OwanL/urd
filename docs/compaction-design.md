# History compaction — design decisions

**Status: direction approved by the owner 2026-11-10. Class shapes deferred until agent tools
are built.**

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
   only on overflow or on request. Pi's fixed budgets apply to every model size.
3. **Existing Urd requirements still hold:**
   - original history stays readable (session-design.md, ARCHITECTURE.md);
   - a transcript marker shows where compaction took effect, with the summary inspectable
     (TRANSCRIPT-DESIGN.md decision 22);
   - failed or cancelled compaction leaves usable context and sets no boundary;
   - compaction never replays tools;
   - the summary call uses the shared request-only `IChatClient`.
4. **Hook:** context preparation before each model request (agent-tools-design.md).

## Open

- Class shapes and storage (marker, summary, cut point keyed to history revision).
- Provider-native replay for summarized spans: drop for the summarized part.
- Sequencing: after agent-tools phase 2, since both touch context building and storage.
