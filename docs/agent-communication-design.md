# Delegation and agent communication — design decisions

**2026-11-16 — active design, not implementation approval.** This records the selected
direction after comparing delegation, handoff, workflow and messaging models in other
harnesses. It does not approve classes, schemas, independent agent-created sessions or a
workflow runtime. Existing [execution](execution-design.md), [agent tool](agent-tools-design.md)
and [architecture](ARCHITECTURE.md) constraints still apply; reconcile their older delegation
sketches before implementation.

## Selected direction

### Delegation is the foundation; messaging is auxiliary

Generalised delegation is primarily **task input → independent work → result**. An
orchestrator routes results into later tasks, parallel work, pipelines, verification and
synthesis. Do not require agents to construct those flows through peer chat, inboxes,
resource leases or polling.

An agent receives a self-contained assignment and returns a final result, failure or blocker.
Intermediate activity may be observable without being injected into the parent's model context.
Programmatic orchestration such as Claude's dynamic workflows is a possible later composition
of this primitive, not a prerequisite for initial subagents.

### One bounded task owns its child until final completion

A subagent invocation uses Urd's existing foreground/background tool mechanics. The child and
all its descendants remain owned by that invocation while running or waiting for an answer.
Stop/cancellation must include the entire owned work tree and join its cleanup.

Returning the final result completes the task, its owning call and the child's addressability.
There is no silently resumable completed child or permanent idle worker. Its transcript may
remain inspectable. This direction does not approve independently delegated sessions.

### Use request/reply for exceptional runtime communication

Provide an **`ask_agent`-style operation** for questions needing another agent's answer:

1. The caller addresses a known eligible agent and supplies its question.
2. A blocking caller waits for the answer; the answer is that ask tool's result.
3. The recipient answers and continues its own task; answering does not finish the recipient.
4. A child may ask more than one question during its bounded task. Its original background
   invocation continues to own it while waiting, so the child is not dangling after a question.
5. Urd's generic `background: true` can make an ask non-blocking. Its eventual answer uses
   ordinary background completion delivery. Backgrounding an ask does not remove a dependency
   if the caller cannot finish without the answer.

Start with **parent ↔ child** communication. Sparse coordination between independent primary
agents, such as asking another session to pause edits, is also a valid target. Direct sibling
messages, broadcasts, agent-wide discovery, shared task boards and resource coordination are
not selected requirements.

A small one-way notification variant remains a candidate for statements that need no answer.
It must not retain a question indefinitely awaiting an unnecessary acknowledgement. Its API is
not settled.

### Foreground children cannot ask their parent

Reject a child-to-parent question immediately when its delegation is foreground. A foreground
parent joins the child's tool call and cannot answer it before that call finishes. Waiting for
that answer would deadlock regardless of durable delivery or wakeups.

Apply this restriction through descendants whose root delegation is foreground. Making the
child's own ask background is not a workaround when the answer is required to complete the task.
The rejection should tell the caller that interactive delegation requires a background child;
it does not automatically rerun or change the invocation.

This restriction does not prohibit foreground delegation for self-contained tasks or ordinary
final results.

### Detect blocking cycles instead of allowing indefinite waits

A blocking ask adds a dependency from the asking agent to the recipient. Before accepting a wait,
reject it if that dependency would return to an agent already blocked on the asker. Report the
cycle as an ordinary tool failure rather than hanging or cancelling unrelated work.

A non-blocking ask does not by itself block its caller. If waiting for its outstanding answer
would complete a cycle, reject that wait. Failed/rejected asks, answers and cancellation release
the relevant dependency. The mechanism must survive Urd's execution/recovery boundaries; it
cannot rely solely on an in-memory graph if blocked work can survive them.

This is a safeguard for an exceptional case, not a workflow scheduler. It does not replace the
foreground parent/child restriction, which is a direct unavoidable dependency.

## Required behavior to design next

These are constraints and questions, not approved implementation mechanics:

- **Recipient states:** define durable presentation and delivery for running model requests,
  executing tools, waiting executions and responses finishing concurrently with a question.
- **Wakeups:** a waiting recipient must be able to receive and answer a question. Do not require
  inbox polling. Reuse persisted inputs and existing request-boundary/wakeup machinery where
  appropriate rather than creating a parallel scheduler.
- **Identity and provenance:** distinguish human input, agent questions/answers and tool results.
  The harness supplies sender identity; model-provided text does not confer user authority.
- **Exactly-once state transitions:** persistence and delivery can be retried, but a question is
  answered once and cancellation is explicit. A tool result is not proof that the recipient read
  or acted on the question unless that is its defined result.
- **Interruption:** ordinary questions are not automatically permission to abort in-flight model
  requests or tools. Prompt delivery versus interruption needs an explicit decision based on the
  recipient-state design.
- **Recovery:** determine how unanswered questions, waiting callers and incoming questions are
  restored after reopening/restart without duplicating effects or silently losing a dependency.
- **Compaction:** outstanding questions retain their identities and full inputs just as other
  background calls do; completion delivery still associates the answer with the original ask.
- **User input and approval:** an agent question is not user consent. Agents cannot answer or
  relay approval for another agent's protected action.

## Deferred title-based addressing

**Owner direction (2026-11-15):** session titles are intended as communication lookup
keys, for example “send message to <title>”. Design this when session communication
is implemented, not as part of title generation. Resolution while a title is pending,
collisions and renames remain undecided; this does not select a title registry or
change durable session identity. The [prepass plan](pruning-plan.md#parallel-title-generation)
allows the main agent to start once pruning settles without waiting for the title.

## Deliberate limits and existing alternatives

- Do not use long-lived background conversations for ordinary finite delegation.
- Do not infer that a completed background call may continue receiving follow-ups.
- Do not add automatic wake flags, polling protocols or persistent peer registries before their
  lifecycle semantics are settled.
- Long-running background Bash remains an escape hatch for timers and external-condition waits.
  It is not the normal answer-delivery mechanism and does not break a dependency cycle.
- Subagent context preparation/pruning should share execution machinery; it must not motivate a
  second agent engine or an unrelated coordination framework.

## Research boundaries

The comparison covered official documentation/source for Codex, Claude Code, Cursor, Copilot,
Gemini CLI, Goose, Google ADK, CrewAI, AutoGen, LangChain/LangGraph, OpenAI Agents SDK and Zed.
It found delegation, request/reply, handoffs, shared state, group chat and programmatic workflows;
there is no universal mailbox model. Claude workflows demonstrate typed programmatic result
routing, parallel/pipeline composition, verification and resumable results. Codex and LangChain
illustrate that delivery, starting a turn and interruption are separate concerns. These findings
inform the direction but do not prove Urd's behavior or qualify a production implementation.

A read-only architecture review identified the foreground parent/child deadlock in
`SessionToolExecution.cs`, `SessionExecution.cs` and `SessionExecutionDriver.cs`. The restriction
above addresses that specific cycle; independent-agent cycles retain the explicit detection rule.
