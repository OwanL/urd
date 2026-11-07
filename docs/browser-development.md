# Browser client development

The first approved browser bite implements `SessionClient`, a typed HTTP client
for the seven existing session operations. It does not yet render a UI, poll,
retain drafts, reconcile unknown submissions or serve browser assets. Preact/Vite
remain the selected UI/build direction, not dependencies of this transport-only bite.

## Checks

Use Node **24.16.0** (`source/ui/.node-version`) and npm **11.13.0**
(`source/ui/package.json`). These match the verified local toolchain; no global
installation or PATH change is required. TypeScript **5.9.3** and Node type definitions
**24.19.2** are locked build/test dependencies. There are no runtime dependencies.
Node runs the tests using its native TypeScript stripping; typechecking is separate.

From `source/ui/`:

```powershell
npm ci --ignore-scripts --no-audit --no-fund
npm run check
npm test
```

For the real-server wire test, first restore/build the Release backend using
[the backend instructions](backend-development.md#prerequisite-and-commands), then:

```powershell
npm run test:integration
```

The fixture launches the built Server with its own temporary data root and port `0`,
reads the actual readiness origin, and reaps only its own process before deleting
that temporary directory. Set `DOTNET_HOST_PATH` to a specific `dotnet` executable
if needed; otherwise it uses `dotnet` on PATH. Fixture termination is explicit,
not evidence of graceful shutdown. CI runs these checks after the backend build/tests.

## Ownership and limits

- `source/ui/sessions/SessionClient.ts` maps directly to the current
  [HTTP contract](server-development.md#session-http-contract). Its constructor does
  no I/O. It stores the supplied origin, not a browser-global selection or connection.
- Every request uses the browser marker and disables caching, cookies and redirects.
  Create/Stop send JSON `{}`; submission fields and opaque list cursors are preserved.
- Only read methods accept cancellation signals. Commands do not retry automatically,
  including after missing receipts, malformed acknowledgements or disconnects.
- HTTP failures throw `SessionRequestError` with the received status and available
  JSON/text body. A broken error body does not erase the status. Stop's unavailable
  receipt stays in the 409 error body; no failure becomes a fabricated refusal.
- Successful responses must contain JSON. Create additionally requires a nonempty
  GUID. The remaining TypeScript records describe the server's wire contract, not a
  general runtime schema decoder. The future UI must match a receipt to the full
  original submission before resolving uncertainty or clearing a draft.

Tests cover request mapping, nullable receipts, exact payloads, failure/no-retry and
read-cancellation boundaries. The real-server test covers creation/listing, actual
serialized records, submission/explicit retry, settlement, segment-local UTF-8
content reads beyond previews, settled Stop and conflicting submission identity.
These are Node HTTP tests, not browser-rendering, CORS, UI-lifetime, provider or
performance qualification. No UI bundle is produced or activated by this bite.

The initial checkpoint passed a locked `npm ci`, strict typechecking, **36/36 HTTP
boundary tests**, the real-server integration test, and the existing **296/296
Release backend tests** without build warnings. Independent acceptance review
approved the production/test boundary; no browser-rendering qualification is implied.
