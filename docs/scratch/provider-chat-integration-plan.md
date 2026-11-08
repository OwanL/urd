# Shared provider requests — implementation plan

2026-11-08. The owner approved starting the shared provider-backed model-request path,
with conversations as its first consumer. Pruning, compaction and titles will reuse that
path without inheriting session state. The owner selected both Responses and compatible
Chat Completions (not native Ollama), and normal requests run until completion or caller
cancellation/Stop rather than inheriting diagnostics' two-minute deadline.

## Ownership and scope

Final ownership split-v3 (explicitly confirmed by both streams): this stream owns
provider-managed request-only `IChatClient`, the SDK/MEAI boundary, provider-native context
codec and its session/execution/storage/schema checkpoint, capability metadata, provider
tests and dependency updates. The concurrent UI stream owns selection/admission integration
AFTER that checkpoint, HTTP/composition and UI. Earlier crossed notes, including v1, are
superseded. Agree the codec contract before storage implementation; coordinate builds
through `urd:dotnet` and commits through explicit staging ownership. Preserve the owner's unstaged `AGENTS.md` edits.

No pruner, discovery tool, compaction, provider fallback, automatic request replay, native
Ollama/Anthropic adapter or agent framework is introduced. Live requests require separate
approval; the prior live OAuth check did not authorize inference or access to old credentials.

## Shared request boundary

- `ProviderManager.CreateChatClient(ProviderConnection expectedBinding)` returns a borrowed,
  request-only `IChatClient`. Standard `ChatOptions.ModelId` and `Reasoning.Effort` carry
  selection. Construction starts no network activity; disposal does not dispose the manager.
- Each enumeration admits against the expected binding, shares existing refresh and grant
  fencing, and remains manager-owned until enumeration/early disposal and callbacks finish.
  Disconnect/shutdown cancels and joins it. Credentials never escape the manager/transport.
- Normal streaming has no Urd wall-clock cutoff; caller deadlines remain supported. Existing
  diagnostics retain their limit. No implicit retries, fallback or provider-hosted tool loop.
- Reuse pinned Microsoft.Extensions.AI.OpenAI 10.11.0 with official OpenAI 2.14.0. Urd owns
  lifecycle, route restrictions, native completion validation and context preservation—not
  reimplementation of ordinary SDK request conversion, OAuth or cryptography.
- Reject unsupported options/content rather than silently dropping them. Reasoning protocol
  support is not proof that a particular model supports an effort. Do not infer capabilities
  from model-name substrings. Keep unknown capability information explicit.

## Native context contract

Use a small codec-owned versioned response envelope, scoped to connection ID, binding
generation, API and exact requested model ID (not a provider-resolved model ID). Retain
ordered native Responses items/IDs/encrypted reasoning; retain
compatible Chat Completions assistant content/calls and supported reasoning representation.
The final update exposes this envelope through `LlmProviderContext.GetResponse(update)`.
`LlmProviderContext.Restore(response)` reconstructs a portable assistant message with opaque
replay metadata. Storage saves it atomically with successful request completion and restores
it instead of flattening that response to generic text/calls; real tool results remain paired
and no completed effect is replayed. Bound opaque payloads explicitly; never truncate them.

Same-scope continuation retains native payloads. An explicit changed binding/API/model
preserves visible text/calls/results while omitting incompatible private reasoning; this
does not introduce a mandatory warning panel or block an idle model switch. Nonportable visible content must not silently disappear.
Native exposure can be retained by the codec, but no discovery implementation is delivered.

Text remains incremental. Completed tool arguments, EOF and generic finish reasons alone
cannot authorize effects. Require positive route-native completion, reject later failures,
join SDK cleanup and manager grant validation before successful enumeration. Auxiliary
`GetResponseAsync` uses this same validated streaming path, without a session/tool loop.

## Verification and delivery

1. Review the boundary and coordinate public/internal API with the UI stream.
2. Implement managed streaming lifetime and SDK/MEAI conversion/codec with focused tests.
3. Exercise both APIs offline through signed fake auth / controlled SDK transports as
   appropriate: terminal failures, fragmented calls, native reload, exact already-sent tool
   results, stale bindings, refresh/disconnect/shutdown, cancellation and held cleanup.
4. This stream integrates actual session SQLite native context first; the UI stream then
   adds selection and HTTP. Joint acceptance covers multi-turn/reopen, harmless tool
   continuation and existing Stop/no-replay fences.
5. Locked restore, Release build and full suite under the lease; independent focused review;
   commit scoped verified work and document remaining qualification limits. No activation of
   a personal-data host or live model/account action without explicit approval.

## Verified checkpoint

Locked restore and Release build passed with no warnings; the full backend suite passed
566/566 tests. Independent reviews approved provider lifetime, codec and native session
persistence after supported findings were corrected. Schema 7 stores native envelopes in
`execution_provider_context`, atomically with successful request completion and tool
call authorization. Tests exercise both APIs through the real SDK/MEAI/manager boundary,
a harmless tool loop, SQLite reopen and exact already-sent result replay without repeating
effects. Public transcript text excludes private reasoning.

Selection/admission and HTTP/UI composition remain with the UI stream after this checkpoint.
The separately authorized live qualification subsequently passed ChatGPT `gpt-6-luna`
and local Ollama `qwen3.5:9b` streaming, harmless tool continuation, SQLite reopen without
repeating effects, and Stop. It exposed and fixed a required namespace description and
compatible Chat's `reasoning` alias; the patched isolated checkpoint passed 573/573 tests
and independent review. ChatGPT revocation was acknowledged and temporary data removed.
See [qualification evidence](../PROVIDER-QUALIFICATION.md#live-shared-request-qualification--2026-11-08)
for request counts and limits. No personal database migration or application activation
was performed. Broader per-model reasoning compatibility remains unknown; no context-window
or pricing variant policy was inferred.
