# Code shape — working engineering guidance

2026-11-05. Maintained by the agents/coordinator, **not an owner-review deliverable**.
The owner reviews concrete code, class/member/method designs and architectural trade-offs.
These are soft engineering review signals, not hard size limits, a lint configuration
or permission to implement a pending design proposal. Adjust them through engineering
judgment rather than escalating routine standards questions to the owner.

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

The owner's [chess engine](https://github.com/OwanL/chess-swing) is a rough readability
reference: concrete domain names, direct operations and cohesive classes. Borrow that clarity,
not its language-specific patterns or class hierarchy. Shorter code is not automatically clearer.

## Naming and discoverability

- Read operation names at their call sites: identify the action and its subject.
  The receiver supplies context, not a missing purpose. A reader should not need to
  open a method body to discover what is submitted, recorded, stopped or returned.
  Do not lengthen names that are already unambiguous in context.
- Names must describe the actual effect and completion guarantee. Submitting input
  is not accepting it; acceptance is not successful execution; saving final text is
  not finishing cleanup. Keep these distinctions visible without encoding the whole
  contract in a method name.
- Name flags and intermediate values for the facts they represent, including units
  where relevant. Do not use one label for different lifecycle states, or hide a
  semantic mismatch behind a friendlier name. Important conditions should read from
  named facts rather than unexplained numeric reader positions or nested ternaries;
  keep decision priority and transaction boundaries visible.
- Name data types for the values or facts they represent, rather than phrases that
  read as instructions. Operations belong in method names. A cohesive record is
  justified by keeping related data together; unclear naming alone is not a reason
  to replace it with a parameter list.
- Use `Llm` for Urd-owned data types describing language-model requests, responses
  or their outcomes, so their source or target is explicit. Avoid ambiguous `Chat`
  or `Output` labels for those concepts. Keep external SDK names such as `IChatClient`
  and `ChatMessage` unchanged; session/run concepts retain their own vocabulary.
- Give each class its own file. Small data-only types (records, enums) of one concept
  share a file named for that concept, such as `Input.cs` or `Messages.cs`. Give each
  type a one-line comment saying what it is.
- File placement and short boundary comments should make purpose, producer/consumer
  roles and important lifecycle guarantees apparent. Do not cram the whole contract
  into a long identifier or require a separate glossary to understand a declaration.

## Control flow and failure paths

- Put a loop's normal termination condition in its header. Use `foreach` / `await foreach`
  for straightforward enumeration; use a meaningful condition for polling or manual reads.
  Do not use `while (true)` or `for (;;)` with exits hidden in the body, or replace them
  with a flag that merely disguises the same control flow.
- Keep progress visible: each pass consumes input, advances a cursor, or awaits a specific
  event. Polling tests need a deadline. A bounded loop may still exit early when the next
  item will not fit; keep that reason beside the exit rather than adding artificial state.
- Separate waiting from processing when their responsibilities differ. For an async stream
  with timed flushes, make the pending read and its cleanup ownership explicit; never start
  a second read while the first is pending or dispose work before it has stopped.
- Name the facts behind decisions and preserve their priority. Prefer guard clauses to
  nested branches, and let SQL ordering name its columns instead of their positions.
- Comments explain constraints and reasons, not what an obvious statement does. Do not
  replace meaningful names with narration or turn an uncertain failure into success.

## Size signals — prompts for review, not targets

| Signal | Review question |
| --- | --- |
| Method exceeds roughly **60 meaningful lines** | Does it contain separable operations or tangled branching? Is the longer form clearer for an explicit state transition/transaction? |
| Substantial class exceeds roughly **500 meaningful lines** | Does it still have one coherent responsibility and lifetime, or has it accumulated unrelated jobs? |
| Constructor needs more than roughly **5 collaborators** | Does this object really coordinate all of them, or is it absorbing another owner's work? |
| Several unrelated mutable-state groups or a broad public surface | Can we explain one owner/invariant for each? Are callers being handed internal controls? |

These size signals are **not automatic failure thresholds**. Comments, data literals,
generated code and test fixtures need judgment, not identical line-count treatment.
Do not create parameter/state bags solely to beat a count. Small classes are fine;
a justified larger class/method can also be the better design. No file-size gate is
proposed. The separate [code-quality gate](backend-development.md#code-quality-gate)
enforces selected analyzer rules during builds and tests; its findings need real fixes
or narrow, documented exceptions, not code reshuffling to beat a metric. Neither these
signals nor analyzer rules override accepted behavioral requirements.

## Initial review format

Present one bite at a time: normally one class or cohesive operation, its relevant
members/signatures inline, a short reason and the architectural choice at hand. Wait
for the owner's response before presenting the next bite. Larger workstream drafts
are internal references, not required reading or whole-document approval requests.
Internally check lifetimes, state/dependency ownership, mutation/persistence boundaries
and concrete flows. Review readability from callers through ordinary and failure paths;
passing tests does not establish that a new reader can understand and safely change the code.
Another agent reviews the proposed design for correctness, cohesion, missing
operations and unnecessary abstractions; the author fixes supported issues. The
coordinator checks integration before presenting the resulting code/API shape to the
owner. Do not expose unreviewed drafts as ready for approval or ask the owner to audit
these guidelines. Implementation also needs tests and independent code review.

Do not specify every private helper in advance or invent classes for UI hooks/scripts.
The first [Core proposal](scratch/initial-core-design.md) starts this process; UI/hosting
and build/startup boundaries follow without reopening the directory tree.

Approval is scoped. After the initial base is accepted, agents should extend its
conventions and tests; ask again for meaningful boundary/behavior changes, not routine
names or local refactoring. The owner explicitly authorizes clear-cut readability fixes,
including names on existing public members whose meaning stays unchanged; do not seek
another vote for each such correction. Ambiguous state meanings are not mechanical renames.
Codebase context helps, but is not a substitute for a short shared guide and review of the
actual implementation.
