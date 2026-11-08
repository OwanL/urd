# Provider management — execution plan

2026-11-08. The owner requested high-level grilling, a rough plan and execution, then
selected **ChatGPT subscription as the first complete usable path**. This plan executes
the agreed headless slice in [provider management design](provider-management-design.md).
It does not authorize live account access or model calls.

## Outcome

Through local HTTP: add a ChatGPT connection, obtain its sign-in URL, complete sign-in,
inspect status, discover models and request one diagnostic response. Saved connections
and credentials survive restart. Then complete the agreed API-key/custom/keyless variants
through the same manager. The agent chat runtime continues using its existing fake client.

## Milestones

1. **Qualify the real library boundaries offline.** Build narrow ChatGPT auth and OpenAI
   model-access adapters with production-path tests. Reuse the existing OAuth/JWT and
   Responses fixture evidence instead of another broad comparison. Pin the researched
   OpenAI 2.14.0, OidcClient 7.1.1, IdentityModel 8.1.0 and JsonWebTokens 8.23.0 packages;
   review regenerated dependency locks. The one-shot diagnostic needs no MEAI adapter.
2. **Persist and coordinate one connection lifecycle.** Add provider configuration,
   private plaintext credentials and last-successful catalog to shared SQLite. Use one
   database owner/connection/lease. Rename the now-shared `SessionStore` to `UrdStore`
   mechanically; do not refactor session execution. Add an atomic v5-to-v6 migration.
   Implement the manager with deterministic tests for stale sign-in/refresh completions,
   credential replacement, disconnect, failures, shutdown and reopen.
3. **Join the ChatGPT-first HTTP workflow.** Add provider routes under existing local
   admission/no-store rules. Keep the OAuth loopback callback separate from JSON API
   client-marker requirements, validating attempt state and identity. Real Kestrel/SQLite
   tests use controlled provider transports, never personal data. Add provider operation
   drain to the host's existing disposal barrier.
4. **Finish the other approved routes.** Official OpenAI API keys and custom-compatible
   Responses/Chat Completions endpoints, including keyless endpoints, share the same
   storage, manager and diagnostic path. No arbitrary OAuth providers, custom auth-header
   framework, compatibility-patch collection, model metadata editor or UI.
5. **Acceptance and handoff.** Run locked restore, focused and full Release tests, independent
   review and relevant offline end-to-end checks. Commit logical verified checkpoints.
   Request explicit authorization before fresh Urd sign-in and a real model-response test;
   report offline versus live qualification separately.

## Small implementation boundaries

- `ProviderManager` owns admission, operation cancellation/joining and coordinated writes.
  Network operations run outside its guard and outside database transactions.
- `ProviderStorage` uses the existing `Database` object and transaction mechanics. Separate
  configuration generation and credential version prevent stale writes without making
  ordinary token rotation invalidate a pending reauthorization.
- Disconnect durably fences use before remote work. Join refresh and preserve its latest
  rotated token for revocation, then clear credentials atomically. Reopening an interrupted
  disconnect must remain fenced; remote failure is not reported as confirmed revocation.
- The auth adapter uses Duende and Microsoft validation, including explicit state/nonce,
  issued-client/account binding and scope checks. No hand-written OAuth/cryptography.
- The model adapter uses the official SDK, route-correct catalog parsing, disabled automatic
  retries and positive terminal evidence. A short prompt/cancellation is not a spending cap.
- Shared DTOs and dependency changes are coordinator-owned. Bounded workers may independently
  implement storage, auth and SDK adapters with disjoint file ownership. One coordinator
  owns integration and serialized `urd:dotnet` restore/build/test runs.

## Verification limits and non-goals

No live Pie credentials, existing Urd data, real browser sign-in, provider account, API key
or paid model call is touched without separate authorization. Offline fixtures must not
have accidental network fallbacks. No provider UI, real agent tools, session/provider
selection, full conversation/context integration, encryption or generic provider framework.
Plaintext persistence deliberately does not protect copied/committed credential data.

Independent rough-plan review found no architectural blocker and identified orchestration
races/restart fencing as the main verification risk. Exact private helpers can follow
implementation evidence; substantive deviations from the agreed scope must be raised.
