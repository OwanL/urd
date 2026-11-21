import type { ComponentChildren } from "preact";
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "preact/hooks";
import type { SessionClient } from "../../sessions/SessionClient.ts";
import type { TranscriptDisplay } from "./TranscriptDisplay.ts";
import { IconButton } from "../../controls/IconButton.tsx";
import { EditableMessage } from "../message-editing/EditableMessage.tsx";
import { CompactionMarker } from "./CompactionMarker.tsx";
import { MessageBody } from "./MessageBody.tsx";
import { ToolRow } from "./tool-calls/ToolRow.tsx";
import { ToolRowInteractionsProvider } from "./tool-calls/ToolRowInteractions.tsx";
import {
  AgentQuestionExchange,
  AgentToolExchange,
  isAgentExchange,
} from "./tool-calls/AgentExchange.tsx";
import { toolCallLabel } from "./tool-calls/ToolRowPresentation.ts";
import { subagentPresentation } from "./tool-calls/SubagentPresentation.ts";
import { SubagentRow } from "./tool-calls/SubagentRow.tsx";
import { TranscriptContentReader } from "./reading/TranscriptContentReader.ts";
import { boundaryResponseStatus } from "../prepass/PrepassRows.tsx";
import type { ResponseStatusContext } from "../prepass/PrepassRows.tsx";
import { isTextEntry } from "../../sessions/transcript/Messages.ts";
import type { TranscriptNavigationState } from "./navigation/TranscriptNavigationStorage.ts";
import { VirtualTranscript } from "./VirtualTranscript.tsx";
import type {
  TranscriptEntryDescriptor,
  TranscriptHistoryPage,
  TranscriptTextFacts,
} from "../../sessions/transcript/Messages.ts";
import "./transcript.css";

const contentReader = new TranscriptContentReader();

export interface ReadingPosition {
  following: boolean;
  anchor: { key: string; top: number } | null;
}

export type RenderPrepass = (entries: readonly TranscriptEntryDescriptor[]) => ComponentChildren;

export interface TranscriptProps {
  children?: ComponentChildren;
  renderPrepass?: RenderPrepass | undefined;
  emptyState?: ComponentChildren;
  client: SessionClient;
  history: TranscriptHistoryPage | null;
  historyRevision?: number | null;
  notice?: string | null;
  display?: TranscriptDisplay | undefined;
  streamId?: string | null;
  sessionId: string;
  visible: boolean;
  position: ReadingPosition | undefined;
  onPositionChange: (position: ReadingPosition) => void;
  transcriptPosition?: TranscriptNavigationState | undefined;
  onTranscriptPositionChange?: (position: TranscriptNavigationState) => void;
  // Catalog-cache names for response headers; absent or ambiguous IDs stay opaque.
  modelNames?: ReadonlyMap<string, string>;
  onChooseModels?: (() => void) | undefined;
}

export function Transcript(props: TranscriptProps) {
  if (props.emptyState)
    return (
      <div class="transcript-area">
        <div
          class="transcript transcript-empty"
          role="region"
          aria-label="Conversation"
          tabIndex={0}
        >
          <div class="empty-transcript">
            {props.emptyState}
            {props.renderPrepass?.([])}
            {props.children}
          </div>
        </div>
      </div>
    );
  if (props.display)
    return (
      <VirtualTranscript
        client={props.client}
        display={props.display}
        streamId={props.streamId ?? null}
        sessionId={props.sessionId}
        historyRevision={props.historyRevision ?? props.history?.historyRevision ?? null}
        notice={props.notice ?? null}
        visible={props.visible}
        modelNames={props.modelNames}
        onChooseModels={props.onChooseModels}
        navigation={props.transcriptPosition}
        onNavigationChange={props.onTranscriptPositionChange ?? ignoreNavigationChange}
        renderPrepass={props.renderPrepass}
      >
        {props.children}
      </VirtualTranscript>
    );
  return <PlainTranscript {...props} />;
}

function ignoreNavigationChange(_position: TranscriptNavigationState): void {}

function PlainTranscript({
  children,
  renderPrepass,
  display,
  streamId = null,
  client,
  history,
  historyRevision,
  sessionId,
  visible,
  position,
  onPositionChange,
  modelNames,
  onChooseModels,
}: TranscriptProps) {
  const RichBody = display?.MessageBody;
  const scroll = useRef<HTMLDivElement>(null);
  const [documentHidden, setDocumentHidden] = useState(
    () => typeof document !== "undefined" && document.hidden,
  );
  const following = useRef(position?.following ?? true);
  const anchor = useRef<ReadingPosition["anchor"]>(position?.anchor ?? null);
  const adjustedScrollTop = useRef<number | null>(null);
  const [root, setRoot] = useState<HTMLDivElement | null>(null);
  const [jump, setJump] = useState(!(position?.following ?? true));
  const [selecting, setSelecting] = useState(false);
  const [shown, setShown] = useState(history);
  const visibleHistory = shown?.sessionId === sessionId ? shown : null;
  const subagents = useMemo(
    () =>
      subagentPresentation(
        visibleHistory?.entries.map((descriptor, rank) => ({ descriptor, rank })) ?? [],
      ),
    [visibleHistory],
  );
  const statusContext: ResponseStatusContext = useMemo(
    () => ({ client, sessionId, modelNames: modelNames ?? new Map(), visible, onChooseModels }),
    [client, sessionId, modelNames, visible, onChooseModels],
  );
  const mountScroll = useCallback((node: HTMLDivElement | null) => {
    scroll.current = node;
    setRoot(node);
  }, []);

  const keepAnchorInPlace = useCallback((node: HTMLDivElement) => {
    const position = anchor.current;
    if (!position) return;
    const target = Array.from(node.querySelectorAll<HTMLElement>("[data-message]")).find(
      (item) => item.dataset.message === position.key,
    );
    if (!target) return;

    const nextScrollTop = node.scrollTop + target.getBoundingClientRect().top - position.top;
    if (nextScrollTop === node.scrollTop) return;
    node.scrollTop = nextScrollTop;
    adjustedScrollTop.current = node.scrollTop;
  }, []);

  useEffect(() => {
    function updateDocumentVisibility() {
      setDocumentHidden(document.hidden);
    }
    document.addEventListener("visibilitychange", updateDocumentVisibility);
    return () => document.removeEventListener("visibilitychange", updateDocumentVisibility);
  }, []);

  useEffect(() => {
    function selectionChanged() {
      const selection = document.getSelection();
      setSelecting(
        Boolean(
          selection && !selection.isCollapsed && scroll.current?.contains(selection.anchorNode),
        ),
      );
    }
    document.addEventListener("selectionchange", selectionChanged);
    return () => document.removeEventListener("selectionchange", selectionChanged);
  }, []);
  useEffect(() => {
    if (!selecting) setShown(history);
  }, [history, selecting]);
  useLayoutEffect(() => {
    const node = scroll.current;
    if (!node) return;
    const observer = new ResizeObserver(() => {
      if (!visible || documentHidden) return;
      if (following.current && !selecting) {
        node.scrollTop = node.scrollHeight;
        adjustedScrollTop.current = node.scrollTop;
      } else if (anchor.current) {
        keepAnchorInPlace(node);
      }
    });
    if (node.firstElementChild) observer.observe(node.firstElementChild);
    for (const item of node.querySelectorAll<HTMLElement>("[data-message]")) observer.observe(item);
    return () => observer.disconnect();
  }, [keepAnchorInPlace, selecting, visibleHistory, visible, documentHidden]);
  useLayoutEffect(() => {
    const node = scroll.current;
    if (!node || selecting || !visible || documentHidden) return;
    if (following.current) {
      node.scrollTop = node.scrollHeight;
      adjustedScrollTop.current = node.scrollTop;
    } else if (anchor.current) {
      keepAnchorInPlace(node);
    }
  }, [keepAnchorInPlace, shown, renderPrepass, children, selecting, visible, documentHidden]);

  function rememberPosition() {
    const node = scroll.current;
    if (!node) return;
    const expectedScrollTop = adjustedScrollTop.current;
    // Lazy bodies can expand between a follow adjustment and its queued scroll events.
    if (expectedScrollTop !== null && node.scrollTop === expectedScrollTop) return;
    adjustedScrollTop.current = null;
    following.current = node.scrollHeight - node.scrollTop - node.clientHeight < 40;
    setJump(!following.current);
    const top = node.getBoundingClientRect().top;
    const first = Array.from(node.querySelectorAll<HTMLElement>("[data-message]")).find(
      (item) => item.getBoundingClientRect().bottom > top,
    );
    anchor.current = first
      ? { key: first.dataset.message!, top: first.getBoundingClientRect().top }
      : null;
    onPositionChange({ following: following.current, anchor: anchor.current });
  }

  return (
    <div class="transcript-area">
      <div
        class="transcript"
        ref={mountScroll}
        onScroll={rememberPosition}
        role="region"
        aria-label="Conversation"
        tabIndex={0}
      >
        <div class="messages">
          <ToolRowInteractionsProvider
            client={client}
            sessionId={sessionId}
            historyRevision={historyRevision ?? visibleHistory?.historyRevision ?? null}
            root={root}
          >
            {visibleHistory?.hasBefore && (
              <p class="history-note">Earlier messages are not shown.</p>
            )}
            {visibleHistory?.entries.map((descriptor, rank) => {
              const subagent = subagents.get(rank);
              if (subagent?.kind === "hidden") return null;
              const entry = descriptor.entry;
              const key = `${entry.executionId}/${entry.entryId}/${entry.kind}/${entry.historyRevision}`;
              const status = boundaryResponseStatus(
                entry.kind,
                descriptor.response,
                descriptor.preparation,
                statusContext,
                entry.historyRevision,
              );
              const label = descriptor.tool
                ? toolCallLabel(descriptor.tool, visibleHistory?.toolLabels ?? {})
                : null;
              const agentExchange = isAgentExchange(descriptor, label);
              const body = (text: TranscriptTextFacts) =>
                display && RichBody ? (
                  <RichBody
                    display={display}
                    streamId={streamId}
                    sessionId={sessionId}
                    entry={entry}
                    text={text}
                    root={root}
                    paused={!visible || documentHidden}
                  />
                ) : (
                  <MessageBody
                    client={client}
                    contentReader={contentReader}
                    sessionId={sessionId}
                    entry={entry}
                    text={text}
                    root={root}
                    paused={selecting || !visible || documentHidden}
                  />
                );
              return (
                <div
                  key={key}
                  data-message={key}
                  class={
                    subagent
                      ? "transcript-subagent-entry"
                      : agentExchange
                        ? "agent-exchange-entry"
                        : descriptor.tool
                          ? "transcript-tool-entry"
                          : undefined
                  }
                >
                  {status.before}
                  {isTextEntry(entry) && descriptor.text ? (
                    descriptor.agentQuestion ? (
                      <AgentQuestionExchange
                        senderTitle={descriptor.agentQuestion.senderTitle}
                        body={body(descriptor.text)}
                      />
                    ) : (
                      <EditableMessage entry={entry} text={descriptor.text}>
                        {body(descriptor.text)}
                      </EditableMessage>
                    )
                  ) : descriptor.compaction ? (
                    <CompactionMarker
                      client={client}
                      sessionId={sessionId}
                      entry={entry}
                      compaction={descriptor.compaction}
                    />
                  ) : descriptor.tool && subagent ? (
                    <SubagentRow
                      client={client}
                      sessionId={sessionId}
                      historyRevision={entry.historyRevision}
                      tool={descriptor.tool}
                      presentation={subagent}
                      root={root}
                      paused={!visible || documentHidden}
                    />
                  ) : descriptor.tool && label && agentExchange ? (
                    <AgentToolExchange
                      client={client}
                      sessionId={sessionId}
                      entry={entry}
                      tool={descriptor.tool}
                      label={label}
                      root={root}
                      paused={!visible || documentHidden}
                    />
                  ) : descriptor.tool ? (
                    <ToolRow
                      tool={descriptor.tool}
                      label={label}
                      completion={entry.kind === "toolCompletion"}
                      root={root}
                      paused={!visible || documentHidden}
                    />
                  ) : null}
                  {status.after}
                </div>
              );
            })}
            {renderPrepass?.(visibleHistory?.entries ?? [])}
            {children}
          </ToolRowInteractionsProvider>
        </div>
      </div>
      {jump && (
        <IconButton
          icon="arrow-down"
          label="Jump to latest"
          class="jump-latest"
          onClick={() => {
            following.current = true;
            onPositionChange({ following: true, anchor: null });
            if (scroll.current) {
              scroll.current.scrollTop = scroll.current.scrollHeight;
              adjustedScrollTop.current = scroll.current.scrollTop;
            }
            setJump(false);
          }}
        />
      )}
    </div>
  );
}
