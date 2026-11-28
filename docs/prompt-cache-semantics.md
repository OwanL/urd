# Prompt-cache semantics

This page defines what prompt-cache behaviour means in Urd. It distinguishes the
provider's reuse of previously processed input from Urd's local conversation,
transport continuation and usage records. It does not promise a cache hit, lower
latency or a subscription/bill reduction.

## Provider-side reuse

A prompt cache is provider-managed reuse of an eligible prefix of model input.
Eligibility and matching are provider- and route-specific. The effective prefix
can include system/developer instructions, serialized messages, tool definitions
and ordering, and other request configuration; unchanged conversation text alone
does not establish a match. Providers decide whether and how much input to reuse.
A cache key or session identifier may influence routing or partitioning where a
route supports it, but is not a command to write or read a cache.

A positive cache read is established only by provider-reported cached-input usage.
A zero, missing or unavailable count is not evidence that the provider attempted
a lookup and rejected it, nor does it identify why no reuse occurred. A cold
request reporting zero cached tokens is not proof that its prefix can never be
reused later.

## Urd prefix stability

During ordinary continuation and dynamic capability discovery, Urd aims to retain
already-sent model-visible content and append new content rather than needlessly
rebuilding an earlier prefix. This is an eligibility-preserving design direction,
not a guarantee that the wire representation remains identical or that the
provider retains the cache.

New starting contexts place shared and role guidance first, then scoped instruction
documents, selected skill references, and finally the runtime environment with its
timestamp last. Document precedence is preserved. Existing boundary snapshots are
not reordered; fresh sessions and contexts rebuilt after compaction use the new order.
Tool declarations remain separate API inputs whose effective placement is provider-owned.

Changes to instructions, messages, attachments, tool definitions/order or request
configuration can change the effective prefix. Explicit history edits and
compaction are supported context boundaries; they can change or shorten what is
sent. Reopening a conversation rebuilds context from durable history. Any reuse
then depends on the rebuilt request matching what the provider recognizes.

HTTP full-history replay and same-connection continuation are different transport
representations of conversation state. Continuation can send only appended input
when Urd confirms that the prior request configuration, input and completed
output match the rebuilt prefix. Otherwise it sends full input without a previous
response ID. This transport optimization is not itself a prompt-cache hit and is
not required for replay correctness.

## Identity and scope

On the built-in ChatGPT route, ordinary conversations send the durable session GUID
as a `session-id`; child sessions use their own GUID. It is stable across reopen
for that conversation. Auxiliary calls, probes, API-key routes and custom routes
do not inherit this behavior. The identifier is a provider-facing correlation or
affinity hint, not authentication, a cache guarantee, a cross-route key or a
billing entitlement. Do not reuse it across account or project boundaries.

Other providers and routes have their own cache semantics. Do not generalize a
ChatGPT-plan observation to API-key, custom, or other provider traffic without
qualifying that route. Urd does not promise cache retention duration or expose a
local prompt-cache store.

## Session summary

The chat's prompt-cache detail presents the last successful conversation turn and
the open session separately. It aggregates conversation-purpose requests only;
other purposes such as titles and background work are excluded. The last-turn row
uses the most recent successful primary or child execution run. Requests for which
usage is absent, estimated, zero-input, or internally inconsistent do not contribute
to the cached share. They remain in the request count, so the detail may show
partial reporting coverage.

The cached percentage is the sum of valid provider-reported cached input divided
by the corresponding valid input-token sum. “Uncached” is the remainder of those
reported input tokens, not an assertion that the provider attempted and missed a
cache lookup. “Not reported” means no eligible request in that summary supplied a
usable provider count; “No completed turn” and “No recorded requests” describe
summary state, not provider cache results. These aggregates describe observed usage,
not a prediction for the next turn.

## Low-reuse warning

The context indicator warns when measured cache reuse is below **60%**, weighted
by input tokens, across at least two comparable continuations after the first
request. Click the triangle for the sampled totals and coverage. The context
count and Compact action are unchanged. The Last turn and Session rows do not
use this filter.

The warning requires consecutive successfully completed requests with valid
provider input and cached-input counts. Urd compares bounded fingerprints of the
actual full-history request: the latest request must append to the preceding
input without changing the earlier input or request configuration. The preceding
measured input must be large enough, and account for enough of the new request,
to make the threshold meaningful. Urd starts over after a prefix, model, account,
provider or context boundary change, a cold gap, or missing or uncertain data.
The comparison state lives only in the open session and does not retain the
prompt text. It starts cold again after reopening.

Only full-history HTTP requests are compared. WebSocket continuation and routes
with unqualified cache reporting do not trigger this warning. A custom Responses
adapter can report a zero even when it has no native cache count. Custom Chat
Completions and Copilot’s OpenAI-compatible routes need a positive reported cache
count on the comparable prefix before low reported reuse is actionable. The
warning does not identify why reuse was low.

## Usage and cost

Usage records keep provider-reported input and cached-input quantities distinct.
Where supported and valid, reported cache-write quantities are also separated by
their supported TTL buckets. Pricing treats cached input and supported cache writes
using their corresponding rates; the remainder is ordinary input. Missing,
negative, contradictory or unsupported quantities leave the affected quote
unknown rather than being silently invented or counted as ordinary input. Live
estimates may provisionally quote input as uncached while the recorded cache
quantity remains unknown; this estimate is not proof of a miss.

API-equivalent usage and cost are not invoices, subscription deductions or proof
of net savings. A cached token count establishes only what the provider reported
for that response. It does not establish that a key caused the hit, quantify
latency saved, or predict future reuse.

## Evidence and references

The [usage-accounting documentation](usage-accounting.md) describes capture,
pricing and uncertainty. The [architecture decision](ARCHITECTURE.md#preserve-llm-visible-prefixes-during-ordinary-continuation-and-discovery)
covers prefix stability. [Provider qualification](provider-qualification.md#chatgpt-plan-prompt-cache-check--2026-11-21)
records bounded synthetic ChatGPT-plan observations, including cases where a key
was accepted but no cache usage was reported; those observations do not establish
a general plan restriction or miss cause.
