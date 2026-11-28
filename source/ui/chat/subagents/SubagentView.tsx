import { useEffect, useRef, useState } from "preact/hooks";
import { RequestError } from "../../http/RequestError.ts";
import type { SessionClient } from "../../sessions/SessionClient.ts";
import type { SubagentViewSnapshot } from "../../sessions/SessionSnapshot.ts";
import type { TranscriptHistoryPage } from "../../sessions/transcript/Messages.ts";
import { UserQuestions } from "../ask-user/UserQuestions.tsx";
import { MessageEditingContext } from "../message-editing/EditableMessage.tsx";
import { Transcript, type ReadingPosition } from "../transcript/Transcript.tsx";
import type { TranscriptDisplay } from "../transcript/TranscriptDisplay.ts";
import type { TranscriptNavigationState } from "../transcript/navigation/TranscriptNavigationStorage.ts";
import { SubagentComposer } from "./SubagentComposer.tsx";
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
  notice: string | null;
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
  notice: rootNotice,
}: SubagentViewProps) {
  const child = path[path.length - 1];
  const id = child?.id;
  const [view, setView] = useState<{
    id: string;
    snapshot: SubagentViewSnapshot;
    notice: number;
    history: TranscriptHistoryPage | null;
  } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [visible, setVisible] = useState(() => !document.hidden);
  const notice = useRef(0);

  useEffect(() => {
    const change = () => setVisible(!document.hidden);
    document.addEventListener("visibilitychange", change);
    return () => document.removeEventListener("visibilitychange", change);
  }, []);

  useEffect(() => {
    if (!id || !connected || !visible) return;
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
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
          snapshot: result,
          notice: ++notice.current,
          history,
        });
        setError(null);
        if (result.live) timer = setTimeout(() => void read(), pollInterval);
      } catch (failure) {
        if (controller.signal.aborted) return;
        if (failure instanceof RequestError && failure.status === 404) {
          setView(null);
          setError("Subagent unavailable.");
          return;
        }
        setError(failure instanceof Error ? failure.message : "Unable to load subagent.");
        timer = setTimeout(() => void read(), pollInterval);
      }
    }
    void read();
    return () => {
      controller.abort();
      clearTimeout(timer);
    };
  }, [client, id, child?.parentId, connected, visible, display]);

  const current = view?.id === id ? view : null;
  return (
    <section id="chat-view" class="subagent-view" role="tabpanel" aria-labelledby={`tab-${rootId}`}>
      <nav class="subagent-breadcrumb" aria-label="Conversation path">
        <button type="button" onClick={() => onNavigate([])}>
          {rootTitle || rootId}
        </button>
        {path.map((item, index) => (
          <span class="subagent-breadcrumb-item" key={item.id}>
            <span aria-hidden="true">/</span>
            {index === path.length - 1 ? (
              <span aria-current="page">{current?.snapshot.title || item.title || item.id}</span>
            ) : (
              <button type="button" onClick={() => onNavigate(path.slice(0, index + 1))}>
                {item.title || item.id}
              </button>
            )}
          </span>
        ))}
      </nav>
      {!connected && (
        <p class="subagent-view-status" role="status">
          Disconnected.
        </p>
      )}
      {connected && error && (
        <p class="subagent-view-status" role="status">
          {error}
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
              historyRevision={current.snapshot.historyRevision}
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
      <UserQuestions client={client} sessionId={rootId} connected={connected} notice={rootNotice} />
      {id && (
        <SubagentComposer
          key={id}
          sessionId={id}
          snapshot={current?.snapshot ?? null}
          active={connected && visible && current !== null}
        />
      )}
    </section>
  );
}
