import type { ComponentChildren } from "preact";
import { useMemo } from "preact/hooks";
import type { SessionClient } from "../../sessions/SessionClient.ts";
import type { ToolDraftStore } from "../../sessions/ToolDrafts.ts";
import { IconButton } from "../../controls/IconButton.tsx";
import type { ResponseStatusContext } from "../prepass/PrepassRows.tsx";
import type { TranscriptNavigationState } from "./navigation/TranscriptNavigationStorage.ts";
import type { TranscriptDisplay } from "./TranscriptDisplay.ts";
import type { RenderPrepass } from "./Transcript.tsx";
import { ToolRowInteractionsProvider } from "./tool-calls/ToolRowInteractions.tsx";
import { VirtualEntry } from "./VirtualEntry.tsx";
import { TranscriptDrafts, useToolTailMotion } from "./TranscriptDrafts.tsx";
import { useTranscriptViewport } from "./viewport/useTranscriptViewport.ts";
import "./transcript.css";

interface VirtualTranscriptProps {
  children?: ComponentChildren;
  renderPrepass?: RenderPrepass | undefined;
  draftStore?: ToolDraftStore | undefined;
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
  draftStore,
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
    tailTop,
    messagesRef,
    followsLatest,
    windowRevision,
    liveWindowStart,
    hasWindow,
    liveWindowEntries,
    windowEntries,
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

  const { motion, enterDraft, enterDurable } = useToolTailMotion(
    sessionId,
    windowRevision,
    liveWindowEntries,
    followsLatest && !paused,
  );
  const entryProps = {
    client,
    display,
    streamId,
    sessionId,
    root,
    paused,
    statusContext,
    onMeasure,
    onSourceCapture,
    onSourceRestore,
    onPublished,
    motion,
  };

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
        <div
          class="messages messages-virtual"
          ref={messagesRef}
          style={{ paddingTop: `${tailTop}px` }}
        >
          <ToolRowInteractionsProvider
            client={client}
            sessionId={sessionId}
            historyRevision={historyRevision}
            root={root}
          >
            <TranscriptDrafts
              store={draftStore}
              sessionId={sessionId}
              entries={windowEntries}
              liveRevision={liveWindowEntries ? windowRevision : null}
              enterDraft={enterDraft}
              durableRows={rows.map(({ key, ...row }) => (
                <VirtualEntry
                  key={key}
                  {...entryProps}
                  {...row}
                  enter={Boolean(row.descriptor.tool) && enterDurable(row.rank - liveWindowStart)}
                />
              ))}
              renderDraft={(draftRow, enter) => (
                <VirtualEntry
                  key={draftRow.key}
                  {...entryProps}
                  saved={undefined}
                  draftRow={draftRow}
                  enter={enter}
                />
              )}
            />
          </ToolRowInteractionsProvider>
          <div class="transcript-tail">
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
