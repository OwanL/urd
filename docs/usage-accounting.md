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
summaries have separate attempt IDs. Streaming checkpoints replace their own amount;
final provider usage replaces the estimate rather than adding another charge.
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

The pre-release schema policy applies: older databases are refused, not migrated.
Do not run a newer-schema build against an active older-schema profile or delete
that profile to make verification pass. Use an isolated temporary profile.

## Pricing and uncertainty

Rates are a checked-in capture of the official OpenAI pricing page on 2026-11-14,
at standard processing rates for exact supported model IDs and the official OpenAI
endpoint. The supported ChatGPT route uses those API-equivalent rates too. Custom
routes and unverified models remain unpriced; no model-prefix guessing or
subscription-price conversion is performed. A provider-resolved model can therefore make a request unpriced.

Input, cached input, exclusive 30-minute/12-hour cache-write buckets and output
are priced separately, including applicable long-context rates. Audio and other
unsupported pricing dimensions are not silently priced as ordinary text. Invalid
or missing quantities remain unknown. Historical request quotes are stored with
the request and are not recomputed when the catalog changes.

Live estimates use local o200k text BPE with approximate message/tool framing.
Missing cache decisions are provisionally quoted as uncached input, but their
recorded quantities remain null. Images, unsupported content, hidden reasoning
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

The composer shows session cost; its popover shows provider composition and cost
sources. Provider legends reveal model totals on hover, focus or click. The workspace's
two cost totals reveal Today and Last 7 days (browser-local midnight), with cumulative
provider-stacked history and per-point model costs. Focus the chart and use arrow keys,
Home or End to inspect samples; the model breakdown is readable HTML. Escape dismisses
the nearest detail first. Request records and rate provenance remain available through
the API, not a ledger accordion in the cost tooltip.

Inspection snapshots stay fixed until reopened while the cost chips continue refreshing.
Chart.js loads on demand; charts and listeners are destroyed on close. `~` marks estimates;
unknown costs stay explicit and only known amounts are plotted. Failed chip refreshes
retain stale values. Hidden documents and inactive session views do not poll.

`GET /api/usage?sessionId=<guid>` reads a session. Global reads require finite ISO
`from`/`to` timestamps spanning at most 31 days. Both range bounds are inclusive;
requests are attributed by start time. Add `detail=false` for totals only. Detailed
responses bound breakdown/history to 64 entries and recent requests to 20. Overflow
provider/model/purpose groups become an explicit `isOther` aggregate, conserving all
amounts and counts. Each history interval includes the same bounded breakdown identities;
its timestamp is the last observed request in that occupied bucket. The chart trims
empty time before and after activity. Totals cover the complete selected range. The route uses ordinary browser admission
and private/no-store response rules.

## Verification

Focused tests cover final replacement without double counting, retries and
compaction, Stop, interrupted recovery, retained accounting after history edits,
unknown/cache/reasoning quantities, failed native responses, provider model tests,
direct unattributed/title/background requests without a session, pricing arithmetic
and bounded HTTP responses. Browser checks cover client mappings and display
calculations. Separate one-off desktop/mobile diagnostics checked hover focus retention, pointer entry and deliberate keyboard activation against an isolated
host with synthetic ledger data. These are not a browser regression suite or evidence
of live-provider accuracy. See [test scope](testing.md) for the verification policy.
