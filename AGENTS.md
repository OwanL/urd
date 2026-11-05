# Urd agent guide

## Current phase: owner-reviewed initial design

- The owner wants to review the initial application structure before production
  implementation, down to important class members and method signatures where useful.
  The rough directory tree is accepted and revisable; it is not approval of every API.
- Present bite-sized proposals case by case: normally one class or one cohesive
  operation, not a multi-type pack. Show the relevant members/signatures inline, a
  short reason and the architectural choice at hand. Wait for the owner's response
  before presenting the next bite; approval covers only the presented scope.
  Full workstream drafts remain internal integration references, not required owner
  reading or whole-document approval requests. Check wider lifetimes, dependencies
  and concurrency/persistence boundaries internally before presenting each bite.
- Before presenting a design to the owner, get independent review by another agent
  and correct supported issues. The coordinator checks cross-stream consistency and
  presents concrete code/class/method shapes and meaningful trade-offs, not raw drafts.
  Engineering standards, obvious defect filtering and guideline maintenance belong to
  the agents/coordinator; do not ask the owner to review process or guideline documents.
- Do not implement a proposed slice before owner approval. Approval applies to the
  described scope, not the whole application or unrelated live-provider/global-machine
  actions. Do not repeatedly seek permission for routine helpers within approved work.
- Once the initial base is accepted, follow its conventions. Revisit substantive
  ownership, public-contract, lifetime or concurrency/persistence changes with the
  owner; ordinary implementation details do not need method-by-method approval.
- Size guidance is a review aid, not a quota or automatic refactoring mandate. Never
  split cohesive state/invariants or introduce wrappers merely to meet a line count.
  UI functions/hooks and scripts need clear ownership too, not artificial C# classes.

## References and boundaries

- [Application plan](APPLICATION-PLAN.md): current rough layout and design-review gate.
- [Session design](SESSION-DESIGN.md) and [execution design](EXECUTION-DESIGN.md): scoped
  behavior decisions. Read the relevant contracts before changing their invariants.
- [Code-shape guidelines](docs/code-shape-guidelines.md) are internal engineering
  guidance with soft review signals, not an owner-approval item or numeric CI gate.
  The [initial Core proposal](docs/scratch/initial-core-design.md) remains a design
  proposal; peer-review acceptance is not owner approval.
- Core remains ASP.NET-free; Server depends on Core. Do not copy historical experiment
  APIs into production or add empty future feature/type scaffolding.
- Keep long-lived docs flat under `docs/` when migrated; `docs/scratch/` is for shared
  project drafts. Disposable agent logs/fixtures belong in owned OS-temp locations.
- Experiments were moved to machine-local scratch; see the README archive note.
  Preserve unrelated files and data. Tests must not use personal sessions/credentials.
