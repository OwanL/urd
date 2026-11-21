import { useEffect, useLayoutEffect, useRef, useState } from "preact/hooks";
import { RequestError } from "../../http/RequestError.ts";
import type { SessionClient } from "../../sessions/SessionClient.ts";
import type { TranscriptHistoryPage } from "../../sessions/transcript/Messages.ts";
import { MessageEditingContext } from "../message-editing/EditableMessage.tsx";
import { Transcript, type ReadingPosition } from "../transcript/Transcript.tsx";
import type { TranscriptDisplay } from "../transcript/TranscriptDisplay.ts";
import type { TranscriptNavigationState } from "../transcript/navigation/TranscriptNavigationStorage.ts";
import { SubagentNavigationContext, type SubagentLink } from "./SubagentNavigation.ts";
import "./subagent-view.css";

interface SubagentViewProps {
  client: SessionClient;
  path: readonly SubagentLink[];
  rootId: string;
  rootTitle: string;
  onNavigate: (path: readonly SubagentLink[]) => void;
  display: TranscriptDisplay | undefined;
  streamId: string | null;
  connected: boolean;
}

const positions = new Map<string, ReadingPosition>();
const transcriptPositions = new Map<string, TranscriptNavigationState>();
const pollInterval = 1000;

function rememberPosition<T>(positions: Map<string, T>, id: string, position: T) {
  positions.delete(id);
  positions.set(id, position);
  if (positions.size > 32) positions.delete(positions.keys().next().value!);
}

export function SubagentView({
  client,
  path,
  rootId,
  rootTitle,
  onNavigate,
  display,
  streamId,
  connected,
}: SubagentViewProps) {
  const child = path[path.length - 1];
  const id = child?.id;
  const [view, setView] = useState<{
    id: string;
    title: string;
    revision: number;
    notice: number;
    history: TranscriptHistoryPage | null;
  } | null>(null);
  const [error, setError] = useState<{ message: string; canRetry: boolean } | null>(null);
  const [visible, setVisible] = useState(() => !document.hidden);
  const [retry, setRetry] = useState(0);
  const heading = useRef<HTMLHeadingElement>(null);
  const notice = useRef(0);

  useLayoutEffect(() => {
    heading.current?.focus({ preventScroll: true });
  }, [id]);

  useEffect(() => {
    const change = () => setVisible(!document.hidden);
    document.addEventListener("visibilitychange", change);
    return () => document.removeEventListener("visibilitychange", change);
  }, []);

  useEffect(() => {
    if (!id || !connected || !visible) return;
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    let failures = 0;
    setError(null);
    async function read() {
      try {
        const result = await client.getSubagentView(id!, controller.signal);
        if (controller.signal.aborted) return;
        if (result.parentId !== child?.parentId) throw new Error("Subagent parent has changed.");
        const history = display
          ? null
          : await client.getHistory(
              id!,
              {
                mode: "latest",
                expectedHistoryRevision: result.historyRevision,
              },
              controller.signal,
            );
        if (controller.signal.aborted) return;
        setView({
          id: id!,
          title: result.title,
          revision: result.historyRevision,
          notice: ++notice.current,
          history,
        });
        failures = 0;
        setError(null);
        if (result.live) timer = setTimeout(() => void read(), pollInterval);
      } catch (failure) {
        if (controller.signal.aborted) return;
        const missing = failure instanceof RequestError && failure.status === 404;
        const canRetry = missing || ++failures > 3;
        setError({
          message: missing ? "Subagent unavailable." : "Unable to load subagent.",
          canRetry,
        });
        if (!canRetry) timer = setTimeout(() => void read(), pollInterval * 2 ** failures);
      }
    }
    void read();
    return () => {
      controller.abort();
      clearTimeout(timer);
    };
  }, [client, id, child?.parentId, connected, visible, display, retry]);

  const current = view?.id === id ? view : null;
  return (
    <section id="chat-view" class="subagent-view" role="tabpanel" aria-labelledby={`tab-${rootId}`}>
      <header class="subagent-view-header">
        <button class="subagent-back" type="button" onClick={() => onNavigate(path.slice(0, -1))}>
          ← Back
        </button>
        <div class="subagent-view-titles">
          <nav class="subagent-breadcrumb" aria-label="Conversation path">
            <button type="button" onClick={() => onNavigate([])}>
              {rootTitle || rootId}
            </button>
            {path.slice(0, -1).map((item, index) => (
              <span class="subagent-breadcrumb-item" key={item.id}>
                <span aria-hidden="true">/</span>
                <button type="button" onClick={() => onNavigate(path.slice(0, index + 1))}>
                  {item.title || item.id}
                </button>
              </span>
            ))}
          </nav>
          <h2 ref={heading} tabIndex={-1}>
            {current?.title || child?.title || id}
          </h2>
        </div>
      </header>
      {!connected && (
        <p class="subagent-view-status" role="status">
          Disconnected.
        </p>
      )}
      {connected && error && (
        <div class="subagent-view-status">
          <span role="status">{error.message}</span>
          {error.canRetry && (
            <button
              type="button"
              onClick={() => {
                heading.current?.focus({ preventScroll: true });
                setRetry((value) => value + 1);
              }}
            >
              Retry
            </button>
          )}
        </div>
      )}
      {connected && !error && !current && (
        <p class="subagent-view-status" role="status">
          Loading subagent…
        </p>
      )}
      {id && current && (
        <SubagentNavigationContext.Provider
          value={(next) => {
            if (next.parentId === id) onNavigate([...path, next]);
          }}
        >
          <MessageEditingContext.Provider value={null}>
            <Transcript
              client={client}
              sessionId={id}
              display={display}
              streamId={streamId}
              history={current.history}
              historyRevision={current.revision}
              notice={String(current.notice)}
              visible={connected && visible}
              position={positions.get(id)}
              onPositionChange={(position) => rememberPosition(positions, id, position)}
              transcriptPosition={transcriptPositions.get(id)}
              onTranscriptPositionChange={(position) =>
                rememberPosition(transcriptPositions, id, position)
              }
            />
          </MessageEditingContext.Provider>
        </SubagentNavigationContext.Provider>
      )}
    </section>
  );
}
