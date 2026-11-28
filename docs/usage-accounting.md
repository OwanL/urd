# API-equivalent usage

Usage is published API-equivalent USD, not invoices or subscription charges.
There are no budgets, limits or enforcement policies.

## Capture and durability

`ProviderManager.CreateChatClient` always returns an accounting client. Every model
attempt is recorded, including direct calls without a session. Callers may attach
`UsageContext` in `ChatOptions.AdditionalProperties` to identify a session, execution
and purpose (for example, `title` or `background`); this labels accounting rather than
enabling it. Metadata is copied and removed before forwarding provider options.
Unattributed calls use purpose `model` and still appear in global totals. Provider
model tests use their separate native-stream observation path.

Conversation exchanges, tool-loop follow-ups, overflow retries and compaction
summaries have separate attempt IDs. Each provider retry also gets its own attempt ID,
without creating a new conversation request. Waiting does not create a usage record.
Streaming checkpoints replace their own amount; final provider usage replaces the estimate
rather than adding another charge.
SDK transport retries are disabled.
Background tool operations do not themselves consume model tokens; model follow-ups
use the ordinary execution path. This does not add a subagent feature.

The `usage_requests` ledger (introduced in schema 25) is separate from conversation
history, without history foreign keys. Editing or replacing history does not erase accounting.
Each record retains provider/connection, model, session/execution, purpose, times,
token quantities, rate source/capture date and quoted rates. Reasoning is retained
as a subset of output, never charged again. Amounts use integer nanodollars.

Provider usage is preserved before terminal response validation, including failed
and incomplete responses. Stop and failure settle observations already received.
Host recovery marks unfinished records incomplete without replaying requests or
inventing final usage. Checkpoints are throttled to 250 ms; a crash can lose progress
since the last checkpoint. Provider-reported usage lost in a disconnected stream
cannot be recovered automatically.

Usage records follow the [durable-data policy](../AGENTS.md): future upgrades must preserve
them from schema 36 onward. Use isolated profiles for verification, never an active user profile.

## Closed-session retention

The detailed ledger supports accounting while a session is open and remains
independent of transcript edits. Final closure has a separate retention boundary:
the delete transaction replaces the closed chat and its helpers' detailed request records
with a compact session summary (times, token counts, cost and error counts). Global totals
retain start time, provider, model, purpose and numeric amounts, but no session, execution
or request identity. Closing a chat therefore neither erases costs from their original
reporting windows nor leaves a readable conversation or per-session request archive.

## Pricing and uncertainty

The reference catalog attaches one text rate card to a canonical model ID, not
one rate per serving provider. It prefers a maker's Standard listing, then a
paid OpenRouter listing for the same base model, then a single Standard listing
from Mistral, then a single unambiguous Standard listing from Copilot or Ollama Cloud. Free listings and rates that
require peak/off-peak or unsupported text pricing rules are not used. Subscription,
regional and optional service-tier adjustments are not applied.

Model IDs and aliases are matched locally. Verified OpenAI/ChatGPT, Copilot,
Ollama Cloud and OpenRouter endpoints get their own ID namespace. A canonical ID
or a unique full name can also resolve without a provider namespace. Case, spaces
and ordinary hyphens are ignored for normalized matching; version separators and
variant names are retained. Short forms such as `6.2sol` require an explicit scoped
alias. Collisions stay unknown; no nearest-name or model-prefix guessing.

The embedded starter snapshot works offline. A miss or a snapshot at least 24 hours
old can trigger one shared background GET of the public [Models.dev v2 chat feed][models-feed],
limited to one attempt per 15 minutes after completion. No connection details,
credentials or prompt data are sent. The v2 feed is experimental, but it exposes
exact cache-write durations and context operators that the stable feed loses. An invalid
feed, download or cache write leaves the last good snapshot in use. Recovery loads a
validated `model-prices.json` from the profile or falls back to the embedded snapshot.
The cache is replaceable and independent of the usage database. Host shutdown cancels
and joins any refresh before releasing its profile lease.

Input, cached input, exclusive 30-minute/12-hour cache-write buckets and output
are priced separately, including a supported long-context breakpoint. Other cache
TTLs, audio and unknown pricing dimensions are not silently priced as ordinary text.
Invalid or missing quantities remain unknown. An attempt keeps its quote unless the
provider reports a different model ID. If it is still unpriced at settlement, it
checks the current catalog once without waiting for refresh. Settled quotes, including
unknown ones, are not recomputed. A wider catalog does not imply support for new
provider routes or new tokenizers.

The build embeds the checked-in `source/backend/harness/usage/model-price-seed.json`;
it does not generate the seed or fetch prices. The seed covers OpenAI, Copilot and Ollama
Cloud plus their reference rows. Runtime refresh considers the wider feed and updates only
the profile cache, not the bundled seed.

[models-feed]: https://models.dev/experimental/v2.0/api.json?type=chat

Live estimates use local o200k text BPE with approximate message/tool framing.
Missing cache decisions are provisionally quoted as uncached input, but their
recorded quantities remain null. A malformed or contradictory cache count leaves the
attempt unpriced instead. A Chat Completions output-limit failure can still report
usage in the next usage-only chunk; reading that chunk does not complete the response
or authorize a tool call. Images, unsupported content, hidden reasoning
and unknown tokenizers are not fabricated from character ratios. Streamed output
is buffered within a fixed limit and tokenized at checkpoints, not on every delta.
Without observed output, a successful finish or reported usage, no final monetary
quote is invented.

**The 5% live-accuracy target is not established.** Local deterministic checks cover
accounting and normalization, not live-provider accuracy. Cache decisions and
hidden output are unknowable while providers withhold them. Do not present these
estimates as a 5%-accurate measurement or a final charge. Live provider qualification
would require separately authorized requests and a representative reference corpus.

## Browser and HTTP

The composer shows the primary session’s lifetime cost, including all nested helpers
and auxiliary calls attributed to them or the primary. Its Session popover uses the
same scope for provider and model costs, followed by cost sources.

The 24h and 7d totals cover the last 24 and 168 hours across the whole workspace,
including unattributed requests. They count each attempt once by start time, not by
adding session totals. Both global windows are available in the composer popover
without changing the Session chip.

History shows cumulative cost by provider. Its legends reveal model totals on hover,
focus or click. Focus the chart and use arrow keys, Home or End to inspect samples;
the per-point model breakdown is readable HTML. Escape dismisses
the nearest detail first. Request records and rate provenance remain available through
the API, not a ledger accordion in the cost tooltip.

Inspection snapshots stay fixed until reopened while the cost chips continue refreshing.
Chart.js loads on demand; charts and listeners are destroyed on close. Amounts display
without estimate markers or unknown-request suffixes. Totals and charts include available
estimates and omit unpriced usage; a dash marks unavailable amounts. The cost popover
notes when totals may be incomplete. Failed chip refreshes retain stale values. Hidden
documents and inactive session views do not poll.

`GET /api/usage?sessionId=<guid>` reads the primary and its descendants, or just that
helper’s own requests if given a helper ID. Global reads require finite ISO
`from`/`to` timestamps spanning at most 31 days. Both range bounds are inclusive;
requests are attributed by start time. Add `detail=false` for totals only. Detailed
responses bound breakdown/history to 64 entries and recent requests to 20. Overflow
provider/model/purpose groups become an explicit `isOther` aggregate, conserving all
amounts and counts. Each history interval includes the same bounded breakdown identities;
its timestamp is the last observed request in that occupied bucket. The chart trims
empty time before and after activity. Totals cover the complete selected range. The one exception: a
session read that only partly overlaps a closed session reports `totalsComplete: false` instead of
credible totals, because closure retained lifetime totals only; do not read the returned zeros as usage
in that range. The route uses ordinary browser admission
and private/no-store response rules.

## Prompt-cache measurement

The composer's Context window tooltip shows **Last turn** and **Session** cache
percentages with uncached input counts. It reads on open, not with the cost chip's
polling. Results based on only some requests are marked partial.

`GET /api/usage/prompt-cache?sessionId=<guid>` reads the session's settled
conversation requests, including tool-loop follow-ups and settled requests in active,
failed or cancelled runs. It excludes active requests and auxiliary purposes such as
compaction, pruning and title generation. `lastTurn` is the latest completed user-input
or continuation run, selected independently of whether it reported cache usage; it is
null if no such run exists. Session closure removes the detailed ledger.

Each aggregate reports total eligible requests and the subset with a valid measured
input/cached-input pair. Input must be positive and cached input must be between zero
and input, inclusive. An explicit zero cached count is measured. Estimated records are
excluded from the measured subset: the existing ledger cannot prove input provenance
for partially reported requests. The token totals sum only measured pairs, so the
weighted cache percentage is `100 * sum(cached input) / sum(input)`, not the average
of request percentages. A zero denominator means unavailable, not a cache miss.
Cached input tokens still occupy context; reuse does not make them free context space.

## Verification

Focused tests cover final replacement without double counting, retries and
compaction, Stop, interrupted recovery, retained accounting after history edits,
unknown/cache/reasoning quantities, failed native responses, provider model tests,
direct unattributed/title/background requests without a session, pricing arithmetic
and bounded HTTP responses. Catalog tests use fixture HTTP responses to check
canonical matching, reference-rate selection, persistent refresh, shutdown and
settlement without modifying historical quotes. The HTTP/SDK pricing flow also
checks exact ledger totals at the long-context boundary and verifies that a
mock refresh changes new quotes, but not settled ones, across a restart. Browser checks cover client mappings and display
calculations. Separate one-off desktop/mobile diagnostics checked hover focus retention, pointer entry and deliberate keyboard activation against an isolated
host with synthetic ledger data. These are not a browser regression suite or evidence
of live-provider accuracy. See [test scope](testing.md) for the verification policy.
