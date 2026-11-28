import { Component } from "preact";
import { useCallback, useLayoutEffect, useRef } from "preact/hooks";
import type { MarkdownSavedSourcePosition } from "../../markdown-rendering/MarkdownMessageView.tsx";
import type {
  TranscriptEntryDescriptor,
  ToolCallLabel,
} from "../../sessions/transcript/Messages.ts";
import type { SessionClient } from "../../sessions/SessionClient.ts";
import { entryKey } from "./viewport/TranscriptRows.ts";
import { EditableMessage } from "../message-editing/EditableMessage.tsx";
import { TranscriptUserImages, transcriptMessageContent } from "./TranscriptUserImages.tsx";
import { CompactionMarker } from "./CompactionMarker.tsx";
import type { TranscriptDisplay } from "./TranscriptDisplay.ts";
import {
  AgentQuestionExchange,
  AgentToolExchange,
  isAgentExchange,
} from "./tool-calls/AgentExchange.tsx";
import { ToolRow } from "./tool-calls/ToolRow.tsx";
import { ToolCallMotion } from "./tool-calls/ToolCallMotion.tsx";
import type { TranscriptDraftRow } from "./TranscriptDrafts.tsx";
import { SubagentRow } from "./tool-calls/SubagentRow.tsx";
import type { SubagentRowPresentation } from "./tool-calls/SubagentPresentation.ts";
import { boundaryResponseStatus } from "../prepass/PrepassRows.tsx";
import type { ResponseStatusContext } from "../prepass/PrepassRows.tsx";

export type RestoreSource = (saved: MarkdownSavedSourcePosition) => boolean;

function EntryRow({
  client,
  display,
  streamId,
  sessionId,
  draftRow,
  descriptor,
  toolLabel,
  subagent,
  rank,
  top,
  measurementEpoch,
  motion,
  enter,
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
  motion: boolean;
  enter: boolean;
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
  onPublished: (key: string, restoredScrollTop?: number) => void;
} & (
  | {
      draftRow: TranscriptDraftRow;
      descriptor?: never;
      toolLabel?: never;
      subagent?: never;
      rank?: never;
      top?: never;
      measurementEpoch?: never;
    }
  | {
      draftRow?: never;
      descriptor: TranscriptEntryDescriptor;
      toolLabel: ToolCallLabel | null;
      subagent: SubagentRowPresentation | undefined;
      rank: number;
      top: number;
      measurementEpoch: number;
    }
)) {
  const row = useRef<HTMLDivElement>(null);
  const key = draftRow ? draftRow.key : entryKey(descriptor.entry);
  const registerSource = useCallback(
    (capture: ((signal: AbortSignal) => Promise<MarkdownSavedSourcePosition | null>) | null) =>
      onSourceCapture(key, capture),
    [key, onSourceCapture],
  );
  const registerRestore = useCallback(
    (restore: RestoreSource | null) => onSourceRestore(key, restore),
    [key, onSourceRestore],
  );
  const published = useCallback(
    (restoredScrollTop?: number) => onPublished(key, restoredScrollTop),
    [key, onPublished],
  );

  useLayoutEffect(() => {
    const element = row.current;
    if (!element || !descriptor || rank === undefined) return;
    let lastHeight = 0;
    const update = () => {
      const height = element.getBoundingClientRect().height;
      if (height === lastHeight) return;
      lastHeight = height;
      onMeasure(descriptor, rank, height);
    };
    const observer = new ResizeObserver(update);
    observer.observe(element);
    update();
    return () => observer.disconnect();
  }, [descriptor, rank, onMeasure, measurementEpoch]);

  const { text, images } = descriptor
    ? transcriptMessageContent(descriptor)
    : { text: null, images: [] };
  const richBody =
    descriptor && text !== null ? (
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
  const agentExchange = descriptor && isAgentExchange(descriptor, toolLabel);
  const entryClass = [
    "transcript-entry",
    subagent?.kind === "hidden" ? "transcript-subagent-hidden" : "",
    subagent && subagent.kind !== "hidden" ? "transcript-subagent-entry" : "",
    draftRow || (descriptor?.tool && !agentExchange && !subagent) ? "transcript-tool-entry" : "",
    draftRow ? "transcript-entry-draft" : "",
    agentExchange ? "agent-exchange-entry" : "",
  ]
    .filter(Boolean)
    .join(" ");
  const status =
    descriptor &&
    boundaryResponseStatus(
      descriptor.entry.kind,
      descriptor.response,
      descriptor.preparation,
      statusContext,
      descriptor.entry.historyRevision,
    );
  const toolRowProps = { root, paused, motion, enter };
  const content = draftRow ? (
    draftRow.draft.name === "subagent" ? (
      <div class="subagent-row">
        {draftRow.subagentHeading !== null && (
          <h3 class="subagent-heading">{draftRow.subagentHeading}</h3>
        )}
        <div class="subagent-title">
          <span>
            Subagent call · {draftRow.draft.state === "drafting" ? "Drafting" : "Drafted"}
          </span>
        </div>
      </div>
    ) : (
      <ToolRow draftRow={draftRow} {...toolRowProps} />
    )
  ) : richBody !== null || images.length > 0 ? (
    descriptor.agentQuestion && richBody !== null ? (
      <AgentQuestionExchange senderTitle={descriptor.agentQuestion.senderTitle} body={richBody} />
    ) : (
      <EditableMessage entry={descriptor.entry} text={text ?? undefined} images={images}>
        <TranscriptUserImages sessionId={sessionId} images={images} root={root} paused={paused} />
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
  ) : subagent?.kind === "hidden" ? null : descriptor.tool && subagent ? (
    <SubagentRow
      client={client}
      sessionId={sessionId}
      historyRevision={descriptor.entry.historyRevision}
      tool={descriptor.tool}
      presentation={subagent}
      root={root}
      paused={paused}
      modelNames={statusContext.modelNames}
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
      {...toolRowProps}
    />
  ) : (
    <div class={`message message-${descriptor.entry.kind === "user" ? "user" : "assistant"}`} />
  );
  return (
    <div
      ref={row}
      class={entryClass}
      data-transcript-entry={descriptor && ""}
      data-entry-key={descriptor && key}
      data-entry-rank={rank}
      style={top === undefined ? undefined : { top: `${top}px` }}
    >
      {status?.before}
      {draftRow || (descriptor.tool && subagent?.kind !== "hidden") ? (
        <ToolCallMotion
          key="tool"
          motion={motion}
          enter={enter}
          preview={draftRow?.draft.state === "drafting" ? draftRow.presentation.preview : null}
          continueBatch={draftRow?.batchedWithNext ?? false}
        >
          {content}
        </ToolCallMotion>
      ) : (
        content
      )}
      {status?.after}
    </div>
  );
}

type EntryRowProps = Parameters<typeof EntryRow>[0];

// Window descriptors are immutable. Don't render unchanged rows when only the composer changes.
export class VirtualEntry extends Component<EntryRowProps> {
  shouldComponentUpdate(next: EntryRowProps): boolean {
    const previous = this.props;
    const keys = Object.keys(next) as (keyof EntryRowProps)[];
    if (keys.length !== Object.keys(previous).length) return true;
    for (const key of keys) if (previous[key] !== next[key]) return true;
    return false;
  }

  render() {
    return <EntryRow {...this.props} />;
  }
}
