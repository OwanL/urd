import { useEffect, useState } from "preact/hooks";
import type { ComponentChildren } from "preact";
import type {
  TranscriptEntryDescriptor,
  TranscriptEntryRef,
  TranscriptToolFacts,
  ToolCallLabel,
  ToolCallTranscript,
} from "../sessions/Messages.ts";
import type { SessionClient } from "../sessions/SessionClient.ts";
import { Icon } from "../components/Icon.tsx";
import { ToolRow } from "./ToolRow.tsx";
import { isToolCallFailed } from "../app/ToolRowPresentation.ts";
import { agentAskTexts, agentAnswerText } from "./AgentCallText.ts";
import "./agent-exchange.css";

// Agent questions and ask/answer calls render as exchanges; their completion rows stay ordinary.
export function isAgentExchange(
  descriptor: TranscriptEntryDescriptor,
  label: ToolCallLabel | null,
): boolean {
  if (!descriptor.tool)
    return descriptor.entry.kind === "user" && Boolean(descriptor.agentQuestion);
  return (
    descriptor.entry.kind === "tool" &&
    (label?.icon === "askAgent" || label?.icon === "answerAgent")
  );
}

// A user entry that carries another agent's question renders as a header over the
// ruled message body instead of a user bubble. It is never editable.
export function AgentQuestionExchange({
  senderTitle,
  body,
}: {
  senderTitle: string | null;
  body: ComponentChildren;
}) {
  return (
    <div class="agent-exchange">
      <div class="tool-row agent-exchange-header">
        <span class="tool-row-icon is-static" role="img" aria-label="Agent question">
          <Icon name="ask" />
        </span>
        <span class="tool-row-title">{senderTitle ?? "An agent"}</span>
        <span class="tool-row-detail">asked</span>
      </div>
      <div class="agent-exchange-body">{body}</div>
    </div>
  );
}

// Answered and unanswered questions wait for the call transcript rather than
// repeating stale detail text as their answer.
interface AgentCallTranscriptState {
  readonly transcript: ToolCallTranscript | null;
  readonly ready: boolean;
}

function useAgentCallTranscript(
  client: SessionClient,
  sessionId: string,
  entry: TranscriptEntryRef,
  tool: TranscriptToolFacts,
): AgentCallTranscriptState {
  // Re-read once the call settles so the exchanged answer becomes available.
  const settled = tool.isClosed && tool.outcome !== null;
  const [state, setState] = useState<AgentCallTranscriptState>({ transcript: null, ready: false });
  useEffect(() => {
    const controller = new AbortController();
    void client
      .readToolCallTranscript(
        sessionId,
        tool.requestId,
        tool.callId,
        { expectedHistoryRevision: entry.historyRevision, returned: false, full: false },
        controller.signal,
      )
      .then((transcript) => {
        if (!controller.signal.aborted) setState({ transcript, ready: true });
      })
      .catch(() => {
        if (!controller.signal.aborted) setState({ transcript: null, ready: true });
      });
    return () => controller.abort();
  }, [
    client,
    sessionId,
    entry.executionId,
    entry.entryId,
    entry.historyRevision,
    tool.requestId,
    tool.callId,
    settled,
  ]);
  return state;
}

// ask_agent and answer_agent calls render through the exchange layout: their
// ordinary ToolRow header followed by the ruled question and answer text.
export function AgentToolExchange({
  client,
  sessionId,
  entry,
  tool,
  label,
  root,
  paused,
}: {
  client: SessionClient;
  sessionId: string;
  entry: TranscriptEntryRef;
  tool: TranscriptToolFacts;
  label: ToolCallLabel;
  root: HTMLElement | null;
  paused: boolean;
}) {
  // The body shows the full text, so the header drops the label's excerpt.
  const headerLabel = {
    ...label,
    title: label.icon === "answerAgent" ? "Answer" : `Asked ${label.title}`,
    detail: null,
  };
  return (
    <div class={`agent-exchange${label.icon === "answerAgent" ? " is-answer" : ""}`}>
      <ToolRow tool={tool} label={headerLabel} root={root} paused={paused} />
      <AgentToolExchangeBody
        client={client}
        sessionId={sessionId}
        entry={entry}
        tool={tool}
        label={label}
      />
    </div>
  );
}

function AgentToolExchangeBody({
  client,
  sessionId,
  entry,
  tool,
  label,
}: {
  client: SessionClient;
  sessionId: string;
  entry: TranscriptEntryRef;
  tool: TranscriptToolFacts;
  label: ToolCallLabel;
}) {
  const { transcript, ready } = useAgentCallTranscript(client, sessionId, entry, tool);
  const loaded = transcript !== null && ready ? transcript : null;
  if (label.icon === "answerAgent") {
    const answer = (loaded ? agentAnswerText(loaded) : null) ?? label.detail;
    return (
      <div class="agent-exchange-body">
        {answer !== null && <p class="agent-exchange-text">{answer}</p>}
      </div>
    );
  }
  const texts = loaded ? agentAskTexts(loaded) : null;
  const prompt = texts?.prompt ?? label.detail;
  const settled = tool.isClosed && tool.outcome !== null;
  // Background asks show only the question; their completion rows stay ordinary.
  if (tool.background) {
    return (
      <div class="agent-exchange-body">
        {prompt !== null && <p class="agent-exchange-text agent-exchange-question">{prompt}</p>}
      </div>
    );
  }
  return (
    <div class="agent-exchange-body">
      {prompt !== null && <p class="agent-exchange-text agent-exchange-question">{prompt}</p>}
      {loaded !== null && settled && tool.outcome === "completed" && (
        <>
          <div class="agent-exchange-label">
            <Icon name="return" />
            Answer
          </div>
          <p class="agent-exchange-text">{texts?.answer ?? ""}</p>
        </>
      )}
      {loaded !== null && settled && tool.outcome !== "completed" && (
        <>
          <div class="agent-exchange-label">Not answered</div>
          {isToolCallFailed(tool) && texts?.answer != null && (
            <p class="agent-exchange-text agent-exchange-error">{texts.answer}</p>
          )}
        </>
      )}
    </div>
  );
}
