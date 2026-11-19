import { useCallback, useLayoutEffect, useRef } from "preact/hooks";
import type { MarkdownSavedSourcePosition } from "../../markdown-rendering/MarkdownMessageView.tsx";
import type {
  TranscriptEntryDescriptor,
  ToolCallLabel,
} from "../../sessions/transcript/Messages.ts";
import { isTextEntry } from "../../sessions/transcript/Messages.ts";
import type { SessionClient } from "../../sessions/SessionClient.ts";
import { entryKey } from "./viewport/TranscriptRows.ts";
import { EditableMessage } from "../message-editing/EditableMessage.tsx";
import { CompactionMarker } from "./CompactionMarker.tsx";
import type { TranscriptDisplay } from "./TranscriptDisplay.ts";
import {
  AgentQuestionExchange,
  AgentToolExchange,
  isAgentExchange,
} from "./tool-calls/AgentExchange.tsx";
import { ToolRow } from "./tool-calls/ToolRow.tsx";
import { boundaryResponseStatus } from "../prepass/PrepassRows.tsx";
import type { ResponseStatusContext } from "../prepass/PrepassRows.tsx";

export type RestoreSource = (saved: MarkdownSavedSourcePosition) => boolean;

export function VirtualEntry({
  client,
  display,
  streamId,
  sessionId,
  descriptor,
  toolLabel,
  rank,
  top,
  root,
  paused,
  saved,
  statusContext,
  onMeasure,
  onSourceCapture,
  onSourceRestore,
  onPublished,
}: {
  client: SessionClient;
  display: TranscriptDisplay;
  streamId: string | null;
  sessionId: string;
  descriptor: TranscriptEntryDescriptor;
  toolLabel: ToolCallLabel | null;
  rank: number;
  top: number;
  root: HTMLElement | null;
  paused: boolean;
  saved: MarkdownSavedSourcePosition | undefined;
  statusContext: ResponseStatusContext;
  onMeasure: (descriptor: TranscriptEntryDescriptor, rank: number, height: number) => void;
  onSourceCapture: (
    key: string,
    capture: ((signal: AbortSignal) => Promise<MarkdownSavedSourcePosition | null>) | null,
  ) => void;
  onSourceRestore: (key: string, restore: RestoreSource | null) => void;
  onPublished: (key: string) => void;
}) {
  const row = useRef<HTMLDivElement>(null);
  const key = entryKey(descriptor.entry);
  const registerSource = useCallback(
    (capture: ((signal: AbortSignal) => Promise<MarkdownSavedSourcePosition | null>) | null) =>
      onSourceCapture(key, capture),
    [key, onSourceCapture],
  );
  const registerRestore = useCallback(
    (restore: RestoreSource | null) => onSourceRestore(key, restore),
    [key, onSourceRestore],
  );
  const published = useCallback(() => onPublished(key), [key, onPublished]);

  useLayoutEffect(() => {
    const element = row.current;
    if (!element) return;
    const update = () => onMeasure(descriptor, rank, element.getBoundingClientRect().height);
    const observer = new ResizeObserver(update);
    observer.observe(element);
    update();
    return () => observer.disconnect();
  }, [descriptor, rank, onMeasure]);

  const role = descriptor.entry.kind === "user" ? "user" : "assistant";
  const text = isTextEntry(descriptor.entry) ? descriptor.text : null;
  const richBody =
    text !== null ? (
      <display.MessageBody
        display={display}
        streamId={streamId}
        sessionId={sessionId}
        entry={descriptor.entry}
        text={text}
        root={root}
        paused={paused}
        {...(saved ? { saved } : {})}
        registerSource={registerSource}
        registerRestore={registerRestore}
        onPublished={published}
      />
    ) : null;
  const agentExchange = isAgentExchange(descriptor, toolLabel);
  const entryClass = [
    "transcript-entry",
    descriptor.tool && !agentExchange ? "transcript-tool-entry" : "",
    agentExchange ? "agent-exchange-entry" : "",
  ]
    .filter(Boolean)
    .join(" ");
  const status = boundaryResponseStatus(
    descriptor.entry.kind,
    descriptor.response,
    descriptor.preparation,
    statusContext,
    descriptor.entry.historyRevision,
  );
  return (
    <div
      ref={row}
      class={entryClass}
      data-transcript-entry=""
      data-entry-key={key}
      data-entry-rank={rank}
      style={{ top: `${top}px` }}
    >
      {status.before}
      {richBody !== null && text !== null ? (
        descriptor.agentQuestion ? (
          <AgentQuestionExchange
            senderTitle={descriptor.agentQuestion.senderTitle}
            body={richBody}
          />
        ) : (
          <EditableMessage entry={descriptor.entry} text={text}>
            {richBody}
          </EditableMessage>
        )
      ) : descriptor.compaction ? (
        <CompactionMarker
          client={client}
          sessionId={sessionId}
          entry={descriptor.entry}
          compaction={descriptor.compaction}
        />
      ) : descriptor.tool && toolLabel && agentExchange ? (
        <AgentToolExchange
          client={client}
          sessionId={sessionId}
          entry={descriptor.entry}
          tool={descriptor.tool}
          label={toolLabel}
          root={root}
          paused={paused}
        />
      ) : descriptor.tool ? (
        <ToolRow
          tool={descriptor.tool}
          label={toolLabel}
          completion={descriptor.entry.kind === "toolCompletion"}
          root={root}
          paused={paused}
        />
      ) : (
        <div class={`message message-${role}`} />
      )}
      {status.after}
    </div>
  );
}
