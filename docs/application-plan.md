# Application structure

The owner accepted this component-led layout as a revisable starting point, not a
folder checklist or approval of every future feature. Responsibilities determine classes
and files; do not create empty modules or one project per concern. The rejected
project-first and vague `systems/` layouts obscured who owned behavior.

## Working layout

```text
urd/
├─ README.md                          short entry point
├─ LICENSE
├─ docs/                              flat guides, decisions and evidence
│  └─ README.md                       documentation index
├─ guidance/                          planned editable runtime guidance
│  ├─ prompts/
│  ├─ agents/
│  └─ skills/
├─ source/
│  ├─ backend/
│  │  ├─ core/
│  │  │  ├─ sessions/                 identity, input, lifecycle and reads
│  │  │  ├─ execution/                model/tool loop, Stop and completion
│  │  │  ├─ providers/                accounts, adapters and request policy
│  │  │  └─ storage/                  durable records and bounded readers
│  │  └─ Urd.Core.csproj               ASP.NET-free library
│  ├─ hosting/server/                 HTTP, local access and composition
│  │  └─ Urd.Server.csproj
│  ├─ ui/                             shared Preact client, grouped by feature
│  ├─ tests/                          cross-component backend tests
│  └─ Urd.slnx
├─ scripts/                           planned setup/build/package automation
├─ data/                              copy-local persistent data
├─ builds/                            proposed distribution output home
└─ .tools/                            proposed acquired-tool home
```

This mixes existing source with planned repository areas; it does not require creating
the planned directories. Shared `assets/` is conditional on a real shared consumer.
UI-only assets stay with UI. Managed attachments are persistent data, not repository assets.
Exact packaged/custom-copy subpaths remain open.

## Responsibilities and future areas

- **Sessions** own live admission, identity and lifecycle. Messaging, assignments/results
  and wakeups have distinct purposes and lifetimes; add subfolders when they help.
  A scheduled wakeup delivers recorded input; it is not an assignment or a second
  execution engine. User questions belong to their originating operation, not a tab.
- **Execution** names the narrow model/tool cycle, not all agent machinery. Shared
  provider accounts and MCP connections must not become execution-owned objects.
- **Context** will own the model's working view, selection, compaction and recall.
  Compaction does not delete readable history. Context selection is not tool permission.
  Starting-context composition combines a shared base with session/assignment-specific
  prompt parts and uses separate discovery/pruning utilities; see the
  [owner direction](ARCHITECTURE.md#compose-starting-context-from-shared-and-session-specific-parts).
  Exact manager APIs, prompt wording and instruction-file selection policies remain open.
- **Guidance** holds bundled editable prompts, agents and skills. A corresponding
  `guidance-loading/` area discovers and reads them. Authored content and loader code
  are separate; existing user/project instructions are not moved into this collection.
  Exact loading/reload behavior remains deferred to the simplest concrete design.
- **Tools and MCP** own implementations, invocation and connection/protocol integration.
  Session tools use ordinary guarded session commands, not duplicate state machines.
- **Storage** owns harness-specific durable facts and attachments. A common database
  does not justify a generic persistence framework; factor shared mechanics only when
  another real consumer needs them. Folder changes do not change transaction authority.
- **Usage** capture and retained summaries belong to the backend; UI presents them.
  Summaries can outlive the conversation without keeping its full history.
- **Settings** meaning and validation stay with domain owners. Provider login is not
  an agent execution; a global configuration/policy manager is not implied.

Context, guidance loading, tools, MCP, usage reporting, coordination and platform/delivery
areas remain unfinished where no implementation exists. Their absence is not a scope cut.
Do not mistake a detailed harness sketch for a complete application design.

## Repository areas

Keep documentation flat, with clear lowercase-kebab-case filenames and a short index.
`docs/scratch/` is optional for pending shared drafts, not generated logs or disposable
agent notes. Temporary work belongs in an owned OS-temp directory. Historical experiment
source is [outside the checkout](product-direction.md#experimental-source-archive);
research paths and commands need that context.

Use `scripts/` for executable automation, not a vague `development/` directory. Runtime
guidance is not project documentation. Backend tests stay under `source/tests/`; UI tests
may stay beside UI. Keep persistent data outside generated, cleaned and packaged inputs.
Do not create a second automation or test tree without a concrete need.

## Initial implementation: owner design review first

Review meaningful new public contracts, responsibilities, lifetimes, concurrency and
persistence boundaries in small pieces before treating them as settled. Present the relevant
class/operation and members inline, a short reason and the actual choice—not a whole draft
or a request to audit engineering process. Obtain independent review and fix supported issues
before presenting the proposal. An approved piece authorizes only its scope.

The initial Core/Server/browser foundation is implemented. Follow its conventions for
routine helpers and wiring; do not request another vote for each private method or clear
readability rename. The owner also authorizes bounded unattended quality improvements under
[code-shape guidance](code-shape-guidelines.md#initial-review-format). That scope does not
approve unrelated proposed features or ambiguous ownership changes.

## Current path and next work

The integrated browser → HTTP → Core → SQLite → managed-provider path is working.
Send freezes selection; exact retry preserves it. Busy input enters the existing execution
at a safe request boundary. Stop addresses a particular execution and acknowledges intent,
not completed cleanup. Saved sessions can be listed without starting execution. Provider
settings, bounded history/not-sent reads, push notices and request usage capture now exist.
See the [index](README.md) for current guides and dated verification evidence.

Transcript stage 2 owns virtualization, images and storage-cache work. Follow its
[design](TRANSCRIPT-DESIGN.md), [stage-1 plan](TRANSCRIPT-HISTORY-PLAN.md) and
[implementation evidence](transcript-history-implementation.md); do not infer that every
proposed stage has shipped. Startup recovery, application tools, tool-body inspection,
an explicit after-run queue, delegation and packaging remain separate work.

Measure tiny output chunks, slow observers, click-to-visible content and memory on the
integrated path. Qualify fidelity of previously sent tool results through real storage;
a provider's successful completion must precede tool effects. Use offline fixtures and
owned data. Live accounts, host activation and external actions require their own scope.
