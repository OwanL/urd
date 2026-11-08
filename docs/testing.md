# Test scope and budget

Tests and their helpers must not exceed application source in size. This is a hard
budget selected by the owner, not a target for compressed formatting. Prefer one
clear behavioral owner per guarantee; remove duplicate scenarios before adding
another fixture or abstraction. Keep independent cases named and failures useful.

Review the budget locally when changing tests. Count physical lines, including
blanks, in first-party C#, JS/TS, JSX/TSX, CSS and HTML under `source/`. Test
directories, test/spec files and helpers count against the test budget.
Tracked and unignored new files count;
deleted files, dependencies, generated outputs, docs and project metadata do not.
Moving helpers elsewhere or packing statements onto one line is not a reduction
in maintenance cost and must not be used to evade the budget.

## Coverage ownership

| Boundary | Representative guarantees |
| --- | --- |
| Storage | Admission/receipt atomicity, durable output ordering, Stop retirement rollback, tool claim/result failure, provider/native-context rollback, Unicode content paging, indexed recent history and keyset listing. |
| Migrations | Frozen schema 1, 4, 5 and 7 upgrade/rollback anchors, including retained identity/history and the latest selection migration. |
| Sessions | Concurrent exact retries, canonical handles, initiating selection, safe-boundary steering, stale Stop isolation, held cleanup/callbacks, failed Stop writes and multi-session shutdown. |
| Execution | Positive completion evidence, buffered Unicode output/deadline flush, write-failure fencing, tool failure/continuation and cleanup before effects. Real SDK loops exercise both provider APIs, native context persistence and reopen. |
| Providers | Issued identity/state/account binding, private context scope, uncertain refresh persistence/recovery, credential mutation and cancellation drain, representative rejected native output and secret-safe errors. |
| HTTP | Representative session/provider wire/status/selection flows, browser admission, disconnected acknowledgements, host draining and exclusive process data ownership. Core policy is not replayed through every route. |
| Browser | Shared transport behavior, client mappings, draft/receipt uncertainty and state transitions; an offline real-server/SDK integration flow. UI-specific suites own their feature state/geometry. |

Use small data tables when cases exercise genuinely different outcomes through the
same setup. Assert observable results, not private task identity or incidental
serialization/planner wording. A wire field, transactional boundary or bounded
query requirement may itself be a contract: retain a direct assertion where it is
needed. Keep cancellation and cleanup barriers deterministic and waits bounded.

## Deliberate reductions

The November 2026 budget pass is **not coverage-equivalent** to the former suite.
It preserves representative risk boundaries rather than every previous scenario:

- Removed repeated CRUD, retry, stop, selection, reopen and read scenarios across
  storage, session and HTTP layers; removed tests of the fake chat client itself.
- Reduced exhaustive per-field/per-error validation, native codec/output shapes,
  provider grants/rotation races, CLI options and HTTP malformed-request matrices.
- Dropped isolated schema 2/3/6 migration fixtures and broad current-schema
  corruption checks. The retained older-version upgrades still traverse later
  migration steps, but do not replace every former fixture/failure point.
- Removed several combined-failure, timer/cancellation-origin, lifecycle and
  exact-buffer-partition variations. Distinct representative rollback, cleanup,
  concurrency and uncertainty tests remain, not exhaustive combinations.

These changes remove test coverage, not application behavior, validation or data
compatibility. Old test counts in development guides describe historical
checkpoints; they are not evidence that every old scenario remains in the current
suite. New regressions should replace a weak scenario or extend an existing
behavioral owner where possible, while respecting the hard budget.

## Verification

Use the pinned toolchains and commands in [backend development](backend-development.md#prerequisite-and-commands)
and [browser development](browser-development.md#toolchain-and-checks): Release
backend tests, UI static checks/unit tests, and the offline SDK integration test.
The size budget is a maintenance constraint, not a substitute for these checks or for
reviewing whether assertions would catch a real regression. Test fixtures must
use owned temporary data and fake providers, never the user's live profile.

The budget pass verified fewer than 7,600 test/helper lines, below
both the original 8,199-line ceiling and the concurrently expanded application's
10,868 lines. Release backend tests (90), UI tests (112), static checks and the
offline integration flow passed. Isolated mutants weakening peer admission,
provider scope isolation and buffered text preservation failed their behavioral
tests; restored controls passed.
