import type { ComponentChildren } from "preact";
import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "preact/hooks";
import type { SessionClient } from "../sessions/SessionClient.ts";
import type { TranscriptDisplay } from "./TranscriptDisplay.ts";
import { IconButton } from "../components/IconButton.tsx";
import { EditableMessage } from "./EditableMessage.tsx";
import { CompactionMarker } from "./CompactionMarker.tsx";
import { ToolRow } from "../chat/ToolRow.tsx";
import { ToolRowInteractionsProvider } from "../chat/ToolRowInteractions.tsx";
import { toolCallLabel } from "./ToolRowPresentation.ts";
import { TranscriptContentReader } from "./TranscriptContentReader.ts";
import { isTextEntry } from "../sessions/Messages.ts";
import type { TranscriptNavigationState } from "../sessions/TranscriptNavigationStorage.ts";
import { VirtualTranscript } from "./VirtualTranscript.tsx";
import type {
  MessageContentCursor,
  TranscriptEntryRef,
  TranscriptHistoryPage,
  TranscriptTextFacts,
} from "../sessions/Messages.ts";
import "../chat/transcript.css";

const contentReader = new TranscriptContentReader();

function sameMessage(left: MessageContentCursor, right: MessageContentCursor): boolean {
  return (
    left.message.executionId === right.message.executionId &&
    left.message.messageId === right.message.messageId &&
    left.message.role === right.message.role &&
    left.message.historyRevision === right.message.historyRevision
  );
}

function sameCursor(left: MessageContentCursor, right: MessageContentCursor): boolean {
  return (
    sameMessage(left, right) &&
    left.segmentIndex === right.segmentIndex &&
    left.utf8Offset === right.utf8Offset
  );
}

function cursorBefore(left: MessageContentCursor, right: MessageContentCursor): boolean {
  return (
    sameMessage(left, right) &&
    (left.segmentIndex < right.segmentIndex ||
      (left.segmentIndex === right.segmentIndex && left.utf8Offset < right.utf8Offset))
  );
}

function MessageBody({
  client,
  contentReader,
  sessionId,
  entry,
  text,
  root,
  paused,
}: {
  client: SessionClient;
  contentReader: TranscriptContentReader;
  sessionId: string;
  entry: TranscriptEntryRef;
  text: TranscriptTextFacts;
  root: HTMLElement | null;
  paused: boolean;
}) {
  const row = useRef<HTMLDivElement>(null);
  const [visible, setVisible] = useState(false);
  const [body, setBody] = useState(() => ({ text: "", cursor: text.from }));
  const committed = useRef(body);
  committed.current = body;
  function commitBody(next: typeof body) {
    committed.current = next;
    setBody(next);
  }
  const latest = useRef(text);
  latest.current = text;
  const [error, setError] = useState<string | null>(null);
  const [batch, setBatch] = useState(0);
  const [more, setMore] = useState(false);
  const reader = useRef<{ notice: () => void; request: () => void } | null>(null);
  const handledBatch = useRef(batch);

  useEffect(() => {
    const node = row.current;
    if (!node) return;
    const observer = new IntersectionObserver(
      ([intersection]) => setVisible(intersection?.isIntersecting ?? false),
      { root },
    );
    observer.observe(node);
    return () => observer.disconnect();
  }, [root]);

  useEffect(() => {
    if (!visible || paused) return;

    const controller = new AbortController();
    let stopped = false;
    let reading = false;
    let trailing = false;
    let current = committed.current;
    let noticedThrough = latest.current.availableThrough;

    async function readPass() {
      if (stopped) return;
      if (reading) {
        trailing = true;
        return;
      }
      reading = true;
      try {
        const target = latest.current;
        if (!sameMessage(current.cursor, target.from) || cursorBefore(current.cursor, target.from))
          current = { text: "", cursor: target.from };
        const tail = target.availableThrough;
        if (!cursorBefore(current.cursor, tail)) {
          if (current !== committed.current) commitBody(current);
          setMore(false);
          return;
        }

        let contentHasMore = true;
        for (let pageCount = 0; pageCount < 4 && cursorBefore(current.cursor, tail); pageCount++) {
          const page = await contentReader.read(sessionId, controller.signal, () =>
            client.getMessageContent(sessionId, current.cursor, controller.signal),
          );
          if (controller.signal.aborted) return;
          if (!sameCursor(page.from, current.cursor))
            throw new Error("Content cursor did not match.");
          if (sameCursor(page.next, current.cursor) && page.hasMore)
            throw new Error("Content did not advance.");
          current = { text: current.text + page.text, cursor: page.next };
          contentHasMore = page.hasMore;
          if (!contentHasMore) break;
        }
        if (controller.signal.aborted) return;
        commitBody(current);
        setMore(contentHasMore && cursorBefore(current.cursor, tail));
        setError(null);
      } catch (cause) {
        if (!controller.signal.aborted) {
          current = committed.current;
          setError(cause instanceof Error ? cause.message : "Content read failed.");
        }
      } finally {
        reading = false;
        if (trailing && !stopped) {
          trailing = false;
          void readPass();
        }
      }
    }

    function request() {
      if (stopped) return;
      if (reading) trailing = true;
      else void readPass();
    }

    const activeReader = {
      notice() {
        const target = latest.current.availableThrough;
        if (sameCursor(noticedThrough, target)) return;
        noticedThrough = target;
        request();
      },
      request,
    };
    reader.current = activeReader;
    void readPass();
    return () => {
      stopped = true;
      if (reader.current === activeReader) reader.current = null;
      controller.abort();
    };
  }, [
    client,
    contentReader,
    sessionId,
    visible,
    paused,
    entry.executionId,
    entry.entryId,
    entry.historyRevision,
    text.from.message.executionId,
    text.from.message.messageId,
    text.from.message.role,
    text.from.message.historyRevision,
    text.from.segmentIndex,
    text.from.utf8Offset,
  ]);

  useEffect(() => {
    reader.current?.notice();
  }, [
    text.availableThrough.message.executionId,
    text.availableThrough.message.messageId,
    text.availableThrough.message.role,
    text.availableThrough.message.historyRevision,
    text.availableThrough.segmentIndex,
    text.availableThrough.utf8Offset,
  ]);

  useEffect(() => {
    if (handledBatch.current === batch) return;
    handledBatch.current = batch;
    reader.current?.request();
  }, [batch]);

  const role = entry.kind === "user" ? "user" : "assistant";
  return (
    <div ref={row} class={`message message-${role}`}>
      <div class="message-text">{body.text}</div>
      {more && (
        <button class="quiet-action" onClick={() => setBatch((value) => value + 1)}>
          Read more
        </button>
      )}
      {error && (
        <div class="inline-error">
          {error} <button onClick={() => setBatch((value) => value + 1)}>Retry read</button>
        </div>
      )}
    </div>
  );
}

export interface ReadingPosition {
  following: boolean;
  anchor: { key: string; top: number } | null;
}

export interface TranscriptProps {
  children?: ComponentChildren;
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
}

export function Transcript(props: TranscriptProps) {
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
        navigation={props.transcriptPosition}
        onNavigationChange={props.onTranscriptPositionChange ?? ignoreNavigationChange}
      >
        {props.children}
      </VirtualTranscript>
    );
  return <PlainTranscript {...props} />;
}

function ignoreNavigationChange(_position: TranscriptNavigationState): void {}

function PlainTranscript({
  children,
  display,
  streamId = null,
  client,
  history,
  historyRevision,
  sessionId,
  visible,
  position,
  onPositionChange,
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
  }, [keepAnchorInPlace, shown, children, selecting, visible, documentHidden]);

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
            {visibleHistory?.entries.map((descriptor) => {
              const entry = descriptor.entry;
              const key = `${entry.executionId}/${entry.entryId}/${entry.kind}/${entry.historyRevision}`;
              return (
                <div
                  key={key}
                  data-message={key}
                  class={descriptor.tool ? "transcript-tool-entry" : undefined}
                >
                  {isTextEntry(entry) && descriptor.text ? (
                    <EditableMessage entry={entry} text={descriptor.text}>
                      {display && RichBody ? (
                        <RichBody
                          display={display}
                          streamId={streamId}
                          sessionId={sessionId}
                          entry={entry}
                          text={descriptor.text}
                          root={root}
                          paused={!visible || documentHidden}
                        />
                      ) : (
                        <MessageBody
                          client={client}
                          contentReader={contentReader}
                          sessionId={sessionId}
                          entry={entry}
                          text={descriptor.text}
                          root={root}
                          paused={selecting || !visible || documentHidden}
                        />
                      )}
                    </EditableMessage>
                  ) : descriptor.compaction ? (
                    <CompactionMarker
                      client={client}
                      sessionId={sessionId}
                      entry={entry}
                      compaction={descriptor.compaction}
                    />
                  ) : descriptor.tool ? (
                    <ToolRow
                      tool={descriptor.tool}
                      label={toolCallLabel(descriptor.tool, visibleHistory?.toolLabels ?? {})}
                      completion={entry.kind === "toolCompletion"}
                      root={root}
                      paused={!visible || documentHidden}
                    />
                  ) : null}
                </div>
              );
            })}
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
