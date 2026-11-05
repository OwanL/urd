# Code shape — proposed review guidelines

2026-11-05. **Draft for owner review.** These are recommendations, not approved size
limits, a lint configuration or permission to implement the pending class proposal.
The owner has required initial design review; the quantitative suggestions remain open.

## What matters more than size

1. **One clear responsibility and lifetime.** A class should own a recognizable piece
   of behavior/state, not collect everything related to sessions or the backend.
   Keep invariants that must change together under one coordinating owner.
2. **Controlled state.** Mutable fields are private; callers issue meaningful commands
   rather than change flags or collections directly. Read models are consistent
   snapshots, not references to live mutable collections. Identify who may write each
   state group and when; private visibility alone does not prevent stale writes.
3. **Explicit boundaries.** Make admission, execution identity/revision checks,
   transaction boundaries, cancellation and settlement visible in the design. Avoid
   hidden constructor I/O or background work; start/stop resources deliberately.
4. **Useful abstraction, not ceremony.** Use an interface for a real boundary or
   variation, not for every class. Composition is the default; retain the selected
   small session hierarchy without inventing unsupported base hooks. No generic
   managers, event buses, factory/service chains or DTO wrappers without a current need.
5. **Readable operations.** A method should describe a coherent operation; extract
   helpers when their names clarify it or provide genuine reuse. Do not scatter an
   atomic operation across tiny methods merely to make each one short.
6. **Behavioral evidence.** Test boundary behavior and invariants, not private helper
   structure. Review the initial implementation against the approved design; document
   material departures instead of allowing the first draft to become accidental policy.

## Size signals — prompts for review, not targets

| Signal | Review question |
| --- | --- |
| Method exceeds roughly **60 meaningful lines** | Does it contain separable operations or tangled branching? Is the longer form clearer for an explicit state transition/transaction? |
| Substantial class exceeds roughly **500 meaningful lines** | Does it still have one coherent responsibility and lifetime, or has it accumulated unrelated jobs? |
| Constructor needs more than roughly **5 collaborators** | Does this object really coordinate all of them, or is it absorbing another owner's work? |
| Several unrelated mutable-state groups or a broad public surface | Can we explain one owner/invariant for each? Are callers being handed internal controls? |

There is **no minimum size, fixed member count or automatic failure threshold**.
Comments, data literals, generated code and test fixtures need judgment, not identical
line-count treatment. Do not create parameter/state bags solely to beat a count.
Small classes are fine; a justified larger class/method can also be the better design.
No CI size gate is proposed. These suggestions do not override accepted behavioral
requirements or justify adding unneeded abstractions.

## Initial review format

Each small pack shows responsibilities/lifetimes, key fields and signatures, dependency
and ownership direction, mutation/persistence boundaries, an example flow, and explicit
trade-offs. Do not specify every private helper in advance or invent classes for UI
hooks/scripts. The first [Core proposal](initial-core-design.md) starts this process;
UI/hosting and build/startup boundaries follow without reopening the directory tree.

Approval is scoped. After the initial base is accepted, agents should extend its
conventions and tests; ask again for meaningful boundary/behavior changes, not routine
names or local refactoring. Codebase context helps, but is not a substitute for a short
shared guide and review of the actual implementation.
