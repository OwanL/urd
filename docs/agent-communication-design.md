# Delegation and agent communication — design decisions and backend status

**2026-11-18 — backend implemented; UI was deferred.** Agent question presentation landed
on 2026-11-19; dedicated subagent presentation has since landed as described below. This records
the selected direction after comparing delegation, handoff, workflow and messaging models in other
harnesses. The backend implements the model-facing tool surface and runtime behavior below. Existing [execution](execution-design.md), [agent tool](agent-tools-design.md)
and [architecture](ARCHITECTURE.md) constraints still apply; reconcile their older delegation
sketches against this implemented behavior.

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

**Disposal (2026-11-16):** a child is finished, and is disposed, once it has produced its final
response and has no pending background calls. A disposed child leaves `list_agents`; asks to it
fail with a clear "agent has finished" error. Its transcript remains inspectable. A child may
deliberately stay alive by keeping a long-running background call such as `sleep`, so it can
answer later questions. That child is still running, so its owning `subagent` call and the
parent's result are delayed until it finishes. Every *live* session remains askable and wakeable.

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

## Runtime behavior (settled 2026-11-16)

The backend implements these runtime behaviors:

- **Recipient states / wakeups (decided 2026-11-16):** delivery reuses the existing opportune
  insertion (steering) machinery. A busy recipient sees the question at its next request
  boundary, alongside tool results; `wake: true` starts an idle or waiting recipient the same
  way new input does. No inbox polling or parallel scheduler.
- **Identity and provenance:** distinguish human input, agent questions/answers and tool results.
  The harness supplies sender identity; model-provided text does not confer user authority.
- **Exactly-once state transitions:** persistence and delivery can be retried, but a question is
  answered once and cancellation is explicit. A tool result is not proof that the recipient read
  or acted on the question unless that is its defined result.
- **Interruption (decided 2026-11-16):** questions never interrupt in-flight model requests or
  tools; they wait for the next boundary. A recipient in a long tool call answers late.
- **Recovery (decided 2026-11-16):** on restart or similar major failure, fail every pending and
  queued question and every waiting ask with a clear error. Nothing is restored or replayed, so
  no question is left dangling.
- **Queue state (decided 2026-11-18):** the question queue, current question and ask/answer
  routing are in-memory runtime state with no table. Transcripts are unchanged: the ask call's
  arguments, its result and the question shown to the recipient record everything. A question is
  written to the recipient's history only when presented, so restart leaves nothing stale to answer.
- **Compaction:** outstanding questions retain their identities and full inputs just as other
  background calls do; completion delivery still associates the answer with the original ask.
- **User input and approval:** an agent question is not user consent. Agents cannot answer or
  relay approval for another agent's protected action.

## Backend model-facing tool surface (settled 2026-11-16; implemented)

The backend exposes the settled `subagent`, `list_agents`, `ask_agent` and `answer_agent` tools.
Only primary sessions can use `ask_user`. Subagents raise questions with their parent via
`ask_agent` or report the blocker if the parent is waiting on a foreground delegation.

### `subagent`

| Input | Meaning |
| --- | --- |
| `bucket` | `small`, `medium`, or `large`; selects a model in the requested tier under the session's enabled-provider policy. |
| `includeUserInput` | Required boolean. `false` inherits no parent history; `true` includes human messages and answered `ask_user` exchanges visible to the parent at delegation. The agent's question, context and choices accompany the human answer as quoted history. Nested delegation must opt in again. There is no recent-history option. |
| `task` | Self-contained assignment. The parent is responsible for including the context the child needs. |

The caller does **not** supply a title. Creation runs Urd's existing pruning/title prepass and
generates the child's immutable title. That title is the communication address. Named roles,
model overrides, caller-selected working directories, full-history inheritance, separate numeric
context budgets, and other invocation options are not part of this proposal.

**Tool availability:** a child runs the same prepass as a primary session. Before every model
request, Urd omits `subagent` if no saved connection is selected by the primary session's
provider policy. Tool admission checks the current selection again, so an in-flight model response
cannot start a child on a newly disabled provider. This applies to descendants as well, without
cancelling already admitted children, changing the main chat provider, or hiding the separate
communication tools. The cached starting context can be reused as providers are toggled.
Delegation defaults to four subagent levels below the primary. Change **Max subagent depth** in
**Settings → General**; zero disables delegation. Agents at or above the limit are not
offered `subagent`, and admission checks the current limit before creating a child. Lowering it
leaves existing agents running. The setting caps depth, not siblings; Stop still owns the whole tree.

**Result (2026-11-16):** the parent's `subagent` call returns the child's final report (its final
response), or an error if it failed or was stopped. Keep the guidance asking for that report
simple; there is no structured result schema.

**Presentation:** the original 2026-11-16 plan deferred dedicated UI and used ordinary tool
calls. The implemented presentation is described under Subagent presentation below.

### `ask_agent`

| Input | Meaning |
| --- | --- |
| `recipient` | Agent's immutable title, or the relationship alias `parent`. |
| `prompt` | Question to ask the agent. |
| `wake` | Whether this question should wake the recipient. |

Every session is askable and wakeable; there are no `canAsk` or `canWake` listing fields. The
foreground-parent restriction and cycle checks are execution rules, not schema capabilities.
If `wake` is false, the ask always runs as a background call, regardless of the recipient's
state: its answer is opportunistic and may never arrive, so blocking on it is never safe. No
mid-call conversion is needed. With `wake: true` the ask blocks normally unless the model passes
the generic `background: true` option, which remains available on any tool.

### `answer_agent`

| Input | Meaning |
| --- | --- |
| `answer` | Response to the recipient's current question. |

Each recipient has **at most one current question**. Further questions are queued in arrival
order and presented only after the current one is answered. The current question is inserted
into the recipient's context at its next opportune boundary, alongside other tool results when
they are being returned. `answer_agent` implicitly answers that current question, so the model
never handles question IDs. The answer becomes the asker's `ask_agent` result; the recipient
then continues its own task.

If the recipient's execution ends or is stopped without answering its presented question, that
question fails with a clear "recipient finished without answering" error. If a conversation run
fails before presenting queued `wake: true` questions, the askers get a not-delivered error;
Urd does not start a new model request for them. For a primary, unpresented `wake: false`
questions remain opportunistic, and explicit Stop still leaves unpresented questions queued.
When a bounded child finishes, fails or is stopped, its mailbox closes and any remaining
questions fail as unanswered. Compaction re-presents the current question, as it does for
outstanding background calls, so `answer_agent` never answers a question the recipient can no
longer see.

### `list_agents`

Takes no proposed arguments and returns all live agents (disposed children are excluded), clearly labelled with title, running state
(`idle`, `waiting for background task`, or `active`), and parent-session title where applicable.
Do not add relationship, `canAsk`, or `canWake` fields: parent title already expresses the
hierarchy, and every session supports asking and waking.

### Addressing

Titles do not change once final. A fresh session passes through built-in placeholder titles:

1. `new session`, or `new session (n)` when several exist.
2. `generating title`, or `generating title (n)` when several exist.
3. The generated, stable, addressable title. If generation fails, the final title is the first
   user prompt (a subagent's task) truncated to the 25-character maximum.

Final titles are unique across stored sessions. A generated or fallback title that duplicates an
existing one receives the lowest free ` (n)` suffix, for example `Fix login bug (2)`. The base is
shortened so the suffixed title still respects the 25-character maximum.

Placeholder titles are not addressable. Communication with such a session fails with a useful
error telling the agent to wait a few seconds and retry. Use the exact final title as the
model-facing identifier, with existing internal
session IDs retained as storage keys. `parent` is a relationship alias, not a lookup for a session
literally named `parent`. Duplicate eligible titles must produce an ambiguity error rather than
silently selecting a session. Resolve the title when accepting the call and retain the internal
identity and original tool arguments for delivery and compaction.



**Owner clarification (2026-11-16):** titles are immutable communication lookup keys, including
generated subagent titles. Exact duplicate matches are an error. There is no mutable-title or
pending-title tool-resolution policy.

## Backend implementation status (2026-11-18)

Backend milestones 1–6 below are implemented. The original change boundaries and acceptance
evidence are retained to make the implemented scope explicit; UI presentation followed later.

| Milestone | Change boundary | Acceptance evidence |
| --- | --- | --- |
| 1. Schema and titles | One schema-version bump (database recreation): session kind, parent session and owning call; title state (`new session`/`generating title` placeholders, final title) with final-title uniqueness; input provenance on `execution_inputs`. No question table (queue state is in memory). Rename `Frontier` to `Large` everywhere. Title fallback to the truncated first prompt and lowest free ` (n)` suffix within 25 characters. | Placeholder numbering; generated, fallback and suffixed titles; duplicates impossible; existing title tests updated; old `frontier` preferences are not silently accepted. |
| 2. Harness tool context | Give harness-owned tools the calling session/execution identity and session services, without exposing them to ordinary tools. | Existing tools unchanged; a harness tool can resolve its caller and parent. |
| 3. `subagent` | Child session type created by the tool: resolve `bucket` through `ModelSelector` to an `LlmSelection`, compose the assignment with optional parent human-input context selected by `includeUserInput`, run the prepass with `SessionKind.Subagent` (tools and instructions inherited from the parent, skills pruned), run to disposal and return the final report. Register children with `SessionManager` for lookup and shutdown, and keep them out of the primary-session tab list. Foreground and background calls; cancelling the call stops the child tree; disposal; restart marks children interrupted like other tools. | Foreground and background results; nested children; Stop cascades through the tree; disposal after the final response with no pending background calls; stay-alive via background call delays the result; restart leaves no live children. |
| 4. `list_agents` | List live sessions with title, state and parent title. | Disposed children and placeholder-only states shown correctly; parent titles. |
| 5. `ask_agent` / `answer_agent` | Resolve `recipient` once (exact final title or `parent` alias; ambiguity is an error) and pin the internal identity with the original arguments. Per-recipient in-memory question queue; present the current question through the existing insertion path with agent provenance; `wake: true` starts idle/waiting recipients like new input. The ask call waits on its question record; `answer_agent` completes that exact call, and failure or cancellation releases it. Reject asks to placeholders, disposed children and from any descendant of a foreground delegation to its waiting ancestors; detect blocking cycles, including a wait that turns an outstanding non-blocking ask into a cycle. Because restart fails all questions, the dependency graph can be in-memory. Fail questions when the recipient's execution ends unanswered, the child is disposed, or on restart. Re-present the current question after compaction. | Busy/idle/waiting recipients; queue ordering; one answer per question; each rejection and failure path; cycle detection; compaction; restart. |
| 6. Guidance | Tool descriptions and simple subagent guidance asking for a final report, in `SubagentSessionGuidance`. | Starting-context composition tests for primary and subagent sessions. |

### Subagent presentation

Subagent calls have dedicated transcript rows with the child title and report. Selecting a
resolved child opens its read-only transcript in the same tab; breadcrumbs return to the primary
or an ancestor, and nested child rows can be opened in turn. Completed children remain accessible
through their parent call. The tab's agent menu lists live descendants and opens their paths;
it is available by hover or explicit control. The child transcript does not provide message
editing or a composer.

### Agent question presentation (2026-11-19)

Questions and answers render as an agent exchange, never as user messages. Each part appears
in chronological order with a tool-row header and a left-ruled body:

- A received question shows "‹sender title› asked" over the question. History descriptors carry
  `agentQuestion: { senderTitle }` for these user entries. They cannot be edited and are not
  prompt-navigation stops.
- An `answer_agent` call shows "Answer" over the full answer.
- A foreground `ask_agent` call shows "Asked ‹recipient›" over the question, then the answer or
  "Not answered". A background ask shows the question only; its completion stays an ordinary
  completion row.

Headers carry no paired answered/waiting status: the transcript does not persist question
outcomes, and the answer row itself records the reply.

## Deliberate limits and existing alternatives

- Do not use long-lived background conversations for ordinary finite delegation.
- Do not infer that a completed background call may continue receiving follow-ups.
- The `wake` flag is settled: `wake: false` answers are opportunistic and may never arrive.
- Do not add polling protocols or persistent peer registries before their lifecycle semantics
  are settled.
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
inform the direction; they do not establish Urd's implementation status or qualify production
readiness. The backend status above describes the implemented Urd behavior.

A read-only architecture review identified the foreground parent/child deadlock in
`SessionToolExecution.cs`, `SessionExecution.cs` and `SessionExecutionDriver.cs`. The restriction
above addresses that specific cycle; independent-agent cycles retain the explicit detection rule.
