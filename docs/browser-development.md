# Browser client development

The browser foundation implements `SessionClient`, a typed HTTP client for the
seven existing session operations, plus fast UI quality tooling. It does not yet
render a UI, poll, retain drafts, reconcile unknown submissions or serve browser
assets. Preact/Vite remain the selected UI/build direction, not dependencies.
There are no runtime dependencies or UI/state scaffolds. As rendering is introduced,
keep components and ordinary Preact state/effects together by feature. Extract hooks
when they clarify actual code, not to prebuild a state framework or folder hierarchy.

## Toolchain and checks

Use Node **24.16.0** (`source/ui/.node-version`) and npm **11.13.0**
(`source/ui/package.json`); no global installation or PATH change is required.
Pinned tools are native TypeScript **7.0.2**, Oxlint **1.92.0** with
**oxlint-tsgolint 7.0.2006**, and independently Oxfmt **0.77.0**.
Node type definitions remain **24.19.2**. Keep optional native packages installed.
Node runs tests with native TypeScript stripping; strict compilation is separate.

From `source/ui/`:

```powershell
npm ci --ignore-scripts --no-audit --no-fund
npm run check
npm test
```

| Command | Boundary |
| --- | --- |
| `npm run check` | Formatter check, one combined native type-aware lint pass, then standalone strict compiler; direct CLI chain, no recursive npm calls. |
| `npm run format` / `npm run format:check` | Write formatting / check formatting without changes. |
| `npm run lint` / `npm run lint:fast` | Combined typed lint / syntax-only feedback. |
| `npm run typecheck` / `npm run typecheck:watch` | Strict compiler once / persistent watch. |
| `npm test` | Node unit tests discovered from `tests/**/*.test.ts`; no frontend test framework. |
| `npm run test:integration` | Separate real-server wire test; requires the Release backend build. |

Formatting pins width 100, double quotes, trailing commas and two-space indentation.
Import/package sorting is disabled; generated `package-lock.json` is ignored by the
formatter. Lint selects hook, accessibility, promise and a small set of bug rules,
not whole presets. Typed lint does not replace strict compilation: do not use
experimental `--type-check` or a JS-plugin bridge. Node tests remain linted;
top-level registrations explicitly use `void` because Node owns their completion.
Async work inside test bodies remains checked.

CI's UI job runs `check` and unit tests independently of the backend. The backend
job retains the real-server integration test, not duplicate UI lint/unit passes.
Existing commit hooks remain unchanged; no auto-stash, fix, build or test hooks
are added.

For integration, first restore/build the Release backend using
[the backend instructions](backend-development.md#prerequisite-and-commands), then
run `npm run test:integration`. The fixture launches the built Server with its own
temporary data root and port `0`, reads the readiness origin, and reaps only its
own process before deleting that directory. Set `DOTNET_HOST_PATH` to a specific
`dotnet` executable if needed; otherwise it uses PATH. Explicit fixture termination
is not evidence of graceful shutdown.

## Evidence and limits

The bounded Windows x64 study used 11 good TS/TSX files (756 lines). Native TS7
preserved the seeded strict diagnostics; the combined Oxlint pass caught all nine
selected lint seeds. Oxfmt matched Prettier byte-for-byte on that corpus and was
idempotent independently of the compiler. Repeated fresh-process medians included
84 ms formatting, 373 ms typed-only lint and 444 ms strict compilation; these are
study measurements, not timings or pass claims for the repository's final gate.
The tiny corpus was startup-dominated, with warm filesystem caches, not disk-cold.
It does not qualify future scale, editors/LSP, Vite/HMR, browsers or other platforms.
TS7 lacks the old compiler API; compiler-API plugins need separate qualification.
Oxfmt is 0.x: keep pins and review upgrades, not assumed universal compatibility.

Normal named `preact/hooks` imports caught conditional hooks and missing effect
dependencies. Aliased and namespace effect dependencies have gaps; do not claim
general Preact alias/namespace support. Keep the control-label rule's default input/
textarea exclusions: overriding them falsely flags valid external `<label htmlFor>`
associations. Unlabeled inputs need browser accessibility checks, not misleading
ARIA labels added to satisfy lint. Static checks alone do not prove accessibility.

The adopted tooling passed a fresh locked, script-disabled install, the complete
static gate, **36/36 unit tests** and the real-server wire test on Windows x64.
Fourteen isolated good/bad fixture checks verified the configured diagnostics and
formatter, including promise checks inside test bodies. Independent review approved.
End-to-end `npm run check` took **1.13 s median** over five fresh-process repeats
(range 1.08–1.19 s; first measured run 1.39 s), including npm/shell startup and all
three static checks. Filesystem caches were warm; this is a baseline, not a fixed
budget or a claim about editor/large-app performance. Linux CI has not been run locally.

## Transport ownership and historical checkpoint

- `source/ui/sessions/SessionClient.ts` maps to the current
  [HTTP contract](server-development.md#session-http-contract). Construction does
  no I/O; it stores the supplied origin, not browser-global selection/connection.
- Every request uses the browser marker and disables caching, cookies and redirects.
  Create/Stop send JSON `{}`; submission fields and opaque list cursors are preserved.
- Only reads accept cancellation. Commands never retry automatically, including
  missing receipts, malformed acknowledgements or disconnects.
- HTTP failures throw `SessionRequestError` with status and available JSON/text body.
  Broken error bodies preserve status; Stop's unavailable receipt stays in the 409
  body. No failure becomes a fabricated refusal.
- Success requires JSON; Create also requires a nonempty GUID. Other records describe
  the wire contract, not general runtime validation. A future UI must match receipts
  to the full original submission before resolving uncertainty or clearing drafts.

Unit tests cover request mapping, nullable receipts, exact payloads, failure/no-retry
and read-cancellation boundaries. Integration covers creation/listing, serialized
records, submission/explicit retry, settlement, segment-local UTF-8 reads beyond
previews, settled Stop and conflicting submission identity. These are Node HTTP
checks, not browser-rendering, CORS, UI-lifetime, provider or performance qualification.
No UI bundle is produced or activated.

The original transport checkpoint passed a locked `npm ci`, strict typechecking,
**36/36 HTTP boundary tests**, real-server integration and **296/296 Release backend
tests** without build warnings. Independent acceptance review approved that
production/test boundary. This historical result does not certify the new tooling
gate or imply browser-rendering qualification.
