import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "preact/hooks";
import { SessionClient } from "../sessions/SessionClient.ts";
import { IconButton } from "../components/IconButton.tsx";
import type { MessageContentCursor, MessagePreview } from "../sessions/Messages.ts";
import type { SessionSnapshot } from "../sessions/SessionSnapshot.ts";

function sameCursor(left: MessageContentCursor, right: MessageContentCursor): boolean {
  return (
    left.segmentIndex === right.segmentIndex &&
    left.utf8Offset === right.utf8Offset &&
    left.message.executionId === right.message.executionId &&
    left.message.messageId === right.message.messageId &&
    left.message.role === right.message.role &&
    left.message.historyRevision === right.message.historyRevision
  );
}

function MessageBody({
  client,
  sessionId,
  preview,
  root,
  paused,
}: {
  client: SessionClient;
  sessionId: string;
  preview: MessagePreview;
  root: HTMLElement | null;
  paused: boolean;
}) {
  const row = useRef<HTMLDivElement>(null);
  const [visible, setVisible] = useState(false);
  const [body, setBody] = useState(() => ({
    text: preview.truncated ? "" : preview.preview,
    cursor: preview.truncated
      ? { message: preview.message, segmentIndex: 0, utf8Offset: 0 }
      : preview.availableThrough,
  }));
  const committed = useRef(body);
  committed.current = body;
  const latest = useRef(preview);
  latest.current = preview;
  const [error, setError] = useState<string | null>(null);
  const [batch, setBatch] = useState(0);
  const [more, setMore] = useState(false);
  useEffect(() => {
    const node = row.current;
    if (!node) return;
    const observer = new IntersectionObserver(
      ([entry]) => setVisible(entry?.isIntersecting ?? false),
      { root },
    );
    observer.observe(node);
    return () => observer.disconnect();
  }, [root]);

  useEffect(() => {
    if (!visible || paused || document.hidden) return;
    const target = latest.current;
    let current = committed.current;
    if (current.cursor.message.historyRevision !== target.message.historyRevision) {
      current = {
        text: target.truncated ? "" : target.preview,
        cursor: target.truncated
          ? { message: target.message, segmentIndex: 0, utf8Offset: 0 }
          : target.availableThrough,
      };
    }
    const tail = target.availableThrough;
    if (
      current.cursor.segmentIndex > tail.segmentIndex ||
      (current.cursor.segmentIndex === tail.segmentIndex &&
        current.cursor.utf8Offset >= tail.utf8Offset)
    ) {
      if (current !== committed.current) setBody(current);
      return;
    }
    const controller = new AbortController();
    async function read() {
      try {
        let hasMore = false;
        for (let pageCount = 0; pageCount < 4; pageCount++) {
          const page = await client.getMessageContent(sessionId, current.cursor, controller.signal);
          if (!sameCursor(page.from, current.cursor))
            throw new Error("Content cursor did not match.");
          if (controller.signal.aborted) return;
          if (sameCursor(page.next, current.cursor) && page.hasMore)
            throw new Error("Content did not advance.");
          current = { text: current.text + page.text, cursor: page.next };
          hasMore = page.hasMore;
          if (!hasMore) break;
        }
        if (controller.signal.aborted) return;
        setBody(current);
        setMore(hasMore);
        setError(null);
      } catch (cause) {
        if (!controller.signal.aborted)
          setError(cause instanceof Error ? cause.message : "Content read failed.");
      }
    }
    void read();
    return () => controller.abort();
  }, [
    client,
    sessionId,
    visible,
    paused,
    preview.availableThrough.segmentIndex,
    preview.availableThrough.utf8Offset,
    preview.message.historyRevision,
    batch,
  ]);

  return (
    <div ref={row} class={`message message-${preview.message.role}`}>
      <div class="message-text">{body.text || preview.preview}</div>
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

export function Transcript({
  client,
  snapshot,
  sessionId,
  visible,
  position,
  onPositionChange,
}: {
  client: SessionClient;
  snapshot: SessionSnapshot | null;
  sessionId: string;
  visible: boolean;
  position: ReadingPosition | undefined;
  onPositionChange: (position: ReadingPosition) => void;
}) {
  const scroll = useRef<HTMLDivElement>(null);
  const following = useRef(position?.following ?? true);
  const anchor = useRef<ReadingPosition["anchor"]>(position?.anchor ?? null);
  const [root, setRoot] = useState<HTMLDivElement | null>(null);
  const [jump, setJump] = useState(!(position?.following ?? true));
  const [selecting, setSelecting] = useState(false);
  const [shown, setShown] = useState(snapshot);
  const mountScroll = useCallback((node: HTMLDivElement | null) => {
    scroll.current = node;
    setRoot(node);
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
    if (!selecting) setShown(snapshot);
  }, [snapshot, selecting]);
  useLayoutEffect(() => {
    const node = scroll.current;
    if (!node) return;
    const observer = new ResizeObserver(() => {
      if (following.current && !selecting) node.scrollTop = node.scrollHeight;
      else if (anchor.current) {
        const target = Array.from(node.querySelectorAll<HTMLElement>("[data-message]")).find(
          (item) => item.dataset.message === anchor.current?.key,
        );
        if (target) node.scrollTop += target.getBoundingClientRect().top - anchor.current.top;
      }
    });
    if (node.firstElementChild) observer.observe(node.firstElementChild);
    return () => observer.disconnect();
  }, [selecting]);
  useLayoutEffect(() => {
    const node = scroll.current;
    if (!node || selecting) return;
    if (following.current) node.scrollTop = node.scrollHeight;
    else if (anchor.current) {
      const target = Array.from(node.querySelectorAll<HTMLElement>("[data-message]")).find(
        (item) => item.dataset.message === anchor.current?.key,
      );
      if (target) node.scrollTop += target.getBoundingClientRect().top - anchor.current.top;
    }
  }, [shown, selecting]);

  function rememberPosition() {
    const node = scroll.current;
    if (!node) return;
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
          {shown?.hasOlderMessages && <p class="history-note">Earlier messages are not shown.</p>}
          {shown?.messages.map((preview) => {
            const key = `${preview.message.executionId}/${preview.message.messageId}/${preview.message.role}`;
            return (
              <div key={key} data-message={key}>
                <MessageBody
                  client={client}
                  sessionId={sessionId}
                  preview={preview}
                  root={root}
                  paused={selecting || !visible}
                />
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
            if (scroll.current) scroll.current.scrollTop = scroll.current.scrollHeight;
            setJump(false);
          }}
        />
      )}
    </div>
  );
}
