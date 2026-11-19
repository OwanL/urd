import type { ComponentChildren } from "preact";
import { useMemo } from "preact/hooks";
import type { SessionClient } from "../../sessions/SessionClient.ts";
import { IconButton } from "../../controls/IconButton.tsx";
import type { ResponseStatusContext } from "../prepass/PrepassRows.tsx";
import type { TranscriptNavigationState } from "./navigation/TranscriptNavigationStorage.ts";
import type { TranscriptDisplay } from "./TranscriptDisplay.ts";
import type { RenderPrepass } from "./Transcript.tsx";
import { ToolRowInteractionsProvider } from "./tool-calls/ToolRowInteractions.tsx";
import { VirtualEntry } from "./VirtualEntry.tsx";
import { useTranscriptViewport } from "./viewport/useTranscriptViewport.ts";
import "./transcript.css";

interface VirtualTranscriptProps {
  children?: ComponentChildren;
  renderPrepass?: RenderPrepass | undefined;
  client: SessionClient;
  display: TranscriptDisplay;
  streamId: string | null;
  sessionId: string;
  historyRevision: number | null;
  notice: string | null;
  visible: boolean;
  modelNames?: ReadonlyMap<string, string> | undefined;
  onChooseModels?: (() => void) | undefined;
  navigation: TranscriptNavigationState | undefined;
  onNavigationChange: (state: TranscriptNavigationState) => void;
}

export function VirtualTranscript({
  children,
  renderPrepass,
  client,
  display,
  streamId,
  sessionId,
  historyRevision,
  notice,
  visible,
  modelNames,
  onChooseModels,
  navigation,
  onNavigationChange,
}: VirtualTranscriptProps) {
  const statusContext: ResponseStatusContext = useMemo(
    () => ({ client, sessionId, modelNames: modelNames ?? new Map(), visible, onChooseModels }),
    [client, sessionId, modelNames, visible, onChooseModels],
  );
  const {
    root,
    scrollRef,
    rememberScroll,
    rows,
    paused,
    onMeasure,
    onSourceCapture,
    onSourceRestore,
    onPublished,
    spacerHeight,
    tailTop,
    tailRef,
    hasWindow,
    loading,
    error,
    retry,
    showJumpLatest,
    jumpLatest,
  } = useTranscriptViewport({
    client,
    sessionId,
    historyRevision,
    notice,
    visible,
    navigation,
    onNavigationChange,
  });

  return (
    <div class="transcript-area">
      <div
        class="transcript transcript-virtual"
        ref={scrollRef}
        onScroll={rememberScroll}
        role="region"
        aria-label="Conversation"
        aria-busy={loading}
        tabIndex={0}
      >
        <div class="messages messages-virtual" style={{ height: `${spacerHeight}px` }}>
          <ToolRowInteractionsProvider
            client={client}
            sessionId={sessionId}
            historyRevision={historyRevision}
            root={root}
          >
            {rows.map(({ key, descriptor, toolLabel, rank, top, saved }) => {
              return (
                <VirtualEntry
                  key={key}
                  client={client}
                  display={display}
                  streamId={streamId}
                  sessionId={sessionId}
                  descriptor={descriptor}
                  toolLabel={toolLabel}
                  rank={rank}
                  top={top}
                  root={root}
                  paused={paused}
                  saved={saved}
                  statusContext={statusContext}
                  onMeasure={onMeasure}
                  onSourceCapture={onSourceCapture}
                  onSourceRestore={onSourceRestore}
                  onPublished={onPublished}
                />
              );
            })}
          </ToolRowInteractionsProvider>
          <div ref={tailRef} class="transcript-tail" style={{ top: `${tailTop}px` }}>
            {renderPrepass?.(rows.map(({ descriptor }) => descriptor))}
            {children}
          </div>
          {!hasWindow && !error && <p class="history-note">Loading messages…</p>}
          {error && (
            <p class="inline-error">
              {error} <button onClick={retry}>Retry</button>
            </p>
          )}
        </div>
      </div>
      {showJumpLatest && (
        <IconButton
          icon="arrow-down"
          label="Jump to latest"
          class="jump-latest"
          onClick={jumpLatest}
        />
      )}
    </div>
  );
}
