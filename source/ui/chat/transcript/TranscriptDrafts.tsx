import type { ComponentChild } from "preact";
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "preact/hooks";
import type { ToolDraft } from "../../sessions/SessionEvents.ts";
import type { ToolDraftStore } from "../../sessions/ToolDrafts.ts";
import type { TranscriptEntryDescriptor } from "../../sessions/transcript/Messages.ts";
import { entryKey } from "./viewport/TranscriptRows.ts";
import { presentToolDraft } from "./tool-calls/ToolDraftPresentation.ts";

const noDrafts: readonly ToolDraft[] = [];

export function TranscriptDrafts({
  store,
  sessionId,
  entries,
  liveRevision,
  durableRows,
  enterDraft,
  renderDraft,
}: {
  store: ToolDraftStore | undefined;
  sessionId: string;
  entries: readonly TranscriptEntryDescriptor[] | null;
  liveRevision: number | null;
  durableRows: ComponentChild[];
  enterDraft: () => boolean;
  renderDraft: (row: TranscriptDraftRow, enter: boolean) => ComponentChild;
}) {
  const [, refresh] = useState(0);
  const drafts = store?.read(sessionId) ?? noDrafts;
  const latest = useRef(drafts);
  latest.current = drafts;
  useLayoutEffect(() => {
    if (!store) return;
    const check = () => {
      if (latest.current !== store.read(sessionId)) refresh((version) => version + 1);
    };
    const unsubscribe = store.subscribe(sessionId, check);
    check();
    return unsubscribe;
  }, [store, sessionId]);

  const loaded = useMemo(() => new Set(entries?.map(({ entry }) => entry.entryId)), [entries]);
  useEffect(() => {
    const adopted = drafts.filter((draft) => loaded.has(draft.entryId));
    if (adopted.length)
      store?.adopt(
        sessionId,
        adopted.map((draft) => draft.entryId),
      );
  }, [drafts, loaded, sessionId, store]);

  const pending = transcriptDraftRows(drafts, loaded, liveRevision, entries);
  if (!pending.length) return durableRows;
  const enter = enterDraft();
  // Keep both kinds of row in one keyed list so adoption reuses the mounted row.
  // Draft-only updates reuse the durable VNodes instead of rendering them again.
  return [...durableRows, ...pending.map((row) => renderDraft(row, enter))];
}

// A loaded/reopened tail is a baseline, not an append. Only animate after the
// same revision's live end has been shown while following.
export function useToolTailMotion(
  sessionId: string,
  revision: number | null,
  entries: readonly TranscriptEntryDescriptor[] | null,
  following: boolean,
) {
  const live = following && entries !== null && revision !== null;
  const last = entries?.at(-1)?.entry;
  const lastKey = last ? entryKey(last) : null;
  const previous = useRef<{ sessionId: string; revision: number; lastKey: string | null } | null>(
    null,
  );
  const before = previous.current;
  useLayoutEffect(() => {
    previous.current = live ? { sessionId, revision, lastKey } : null;
  }, [live, sessionId, revision, lastKey]);
  const continued = live && before?.sessionId === sessionId && before.revision === revision;
  const boundary =
    continued && before.lastKey !== null
      ? entries.findIndex(({ entry }) => entryKey(entry) === before.lastKey)
      : -1;
  return {
    motion: live,
    enterDraft: () =>
      live && previous.current?.sessionId === sessionId && previous.current.revision === revision,
    enterDurable: (index: number) =>
      continued && (boundary >= 0 || before.lastKey === null) && index > boundary,
  };
}

export type TranscriptDraftRow = ReturnType<typeof transcriptDraftRows>[number];

function transcriptDraftRows(
  drafts: readonly ToolDraft[],
  loaded: ReadonlySet<string>,
  historyRevision: number | null,
  entries: readonly TranscriptEntryDescriptor[] | null,
) {
  if (!drafts.length || historyRevision === null) return [];
  const requests = new Map<string, ToolDraft[]>();
  for (const draft of drafts) {
    if (loaded.has(draft.entryId)) continue;
    const key = JSON.stringify([draft.executionId, draft.requestId]);
    const request = requests.get(key) ?? [];
    request.push(draft);
    requests.set(key, request);
  }
  return [...requests.values()].flatMap((request) => {
    request.sort((a, b) => a.order - b.order);
    return request.map((draft, index) => ({
      draft,
      presentation: presentToolDraft(draft),
      key: entryKey({ ...draft, kind: "tool", historyRevision }),
      batchedWithPrevious: index > 0,
      batchedWithNext: index < request.length - 1,
      subagentHeading:
        draft.name !== "subagent"
          ? null
          : request[index - 1]?.name === "subagent" ||
              (index === 0 &&
                entries?.at(-1)?.entry.kind === "tool" &&
                entries.at(-1)?.entry.executionId === draft.executionId &&
                entries.at(-1)?.tool?.requestId === draft.requestId &&
                entries.at(-1)?.tool?.name === "subagent")
            ? null
            : request[index + 1]?.name === "subagent"
              ? "Subagents"
              : "Subagent",
    }));
  });
}
