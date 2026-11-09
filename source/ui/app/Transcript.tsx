import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "preact/hooks";
import type { SessionClient } from "../sessions/SessionClient.ts";
import { IconButton } from "../components/IconButton.tsx";
import { TranscriptContentReader } from "./TranscriptContentReader.ts";
import type {
  MessageContentCursor,
  TranscriptEntryRef,
  TranscriptHistoryPage,
  TranscriptTextFacts,
  TranscriptToolFacts,
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

function ToolEntry({ tool }: { tool: TranscriptToolFacts }) {
  const status =
    tool.outcome ?? (tool.started ? "Running" : tool.isClosed ? "Not started" : "Waiting");
  return (
    <div class="message message-assistant">
      <div class="message-text">
        {tool.name ?? "Tool"} · {status}
      </div>
    </div>
  );
}

export interface ReadingPosition {
  following: boolean;
  anchor: { key: string; top: number } | null;
}

export function Transcript({
  client,
  history,
  sessionId,
  visible,
  position,
  onPositionChange,
}: {
  client: SessionClient;
  history: TranscriptHistoryPage | null;
  sessionId: string;
  visible: boolean;
  position: ReadingPosition | undefined;
  onPositionChange: (position: ReadingPosition) => void;
}) {
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
  }, [keepAnchorInPlace, selecting, visibleHistory]);
  useLayoutEffect(() => {
    const node = scroll.current;
    if (!node || selecting) return;
    if (following.current) {
      node.scrollTop = node.scrollHeight;
      adjustedScrollTop.current = node.scrollTop;
    } else if (anchor.current) {
      keepAnchorInPlace(node);
    }
  }, [keepAnchorInPlace, shown, selecting]);

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
          {visibleHistory?.hasBefore && <p class="history-note">Earlier messages are not shown.</p>}
          {visibleHistory?.entries.map((descriptor) => {
            const entry = descriptor.entry;
            const key = `${entry.executionId}/${entry.entryId}/${entry.kind}`;
            return (
              <div key={key} data-message={key}>
                {descriptor.text ? (
                  <MessageBody
                    client={client}
                    contentReader={contentReader}
                    sessionId={sessionId}
                    entry={entry}
                    text={descriptor.text}
                    root={root}
                    paused={selecting || !visible || documentHidden}
                  />
                ) : descriptor.tool ? (
                  <ToolEntry tool={descriptor.tool} />
                ) : null}
              </div>
            );
          })}
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
