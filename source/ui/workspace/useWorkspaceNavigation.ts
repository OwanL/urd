import { useCallback, useEffect, useRef, useState } from "preact/hooks";
import type { LlmSelection } from "../providers/LlmSelection.ts";
import type { SessionClient } from "../sessions/SessionClient.ts";
import { RequestError } from "../http/RequestError.ts";
import { errorMessage } from "../http/ErrorMessages.ts";
import { moveTab } from "./tabs/TabOrder.ts";
import { newChat } from "../chat/ChatState.ts";
import type { SessionIdPage } from "../sessions/SessionIdPage.ts";
import type { PersistedWorkspace } from "./usePersistedWorkspace.ts";
import { enumerateSessionPages } from "../sessions/SessionPages.ts";

const idPattern = /^[\da-f]{8}(-[\da-f]{4}){3}-[\da-f]{12}$/i;

function hashSessionId(): string | null {
  const hash = window.location.hash.slice(1);
  return idPattern.test(hash) ? hash.toLowerCase() : null;
}

function syncHash(activeId: string | null): void {
  window.history.replaceState(
    null,
    "",
    `${window.location.pathname}${window.location.search}${activeId ? `#${activeId}` : ""}`,
  );
}

export function useWorkspaceNavigation(
  client: SessionClient,
  {
    state,
    loaded,
    access,
    forgetSessions,
    isClosed,
  }: Pick<PersistedWorkspace, "state" | "loaded" | "access" | "forgetSessions" | "isClosed">,
  create: () => Promise<void>,
  isSnoozed: (id: string) => boolean,
) {
  const [ready, setReady] = useState(false);
  const [lastUsedSelection, setLastUsedSelection] = useState<LlmSelection | null>(null);
  const discovered = useRef(new Set<string>());
  const [error, setError] = useState<string | null>(null);
  const forget = useCallback(
    (ids: Iterable<string>) => {
      const removed = new Set<string>();
      for (const id of ids) {
        if (isClosed(id)) continue;
        discovered.current.delete(id);
        removed.add(id);
      }
      if (removed.size) forgetSessions(removed);
    },
    [forgetSessions, isClosed],
  );
  const open = useCallback(
    (id: string) => {
      if (isClosed(id) || isSnoozed(id)) return;
      const workspace = access.read();
      if (workspace.activeId === id) return;
      access.save({
        ...workspace,
        activeId: id,
        tabs: workspace.tabs.includes(id) ? workspace.tabs : [...workspace.tabs, id],
        chats: { ...workspace.chats, [id]: workspace.chats[id] ?? newChat(null) },
      });
    },
    [access, isClosed, isSnoozed],
  );
  const reorder = useCallback(
    (id: string, beforeId: string | null) => {
      const workspace = access.read();
      const ordered = moveTab(
        workspace.tabs.map((tabId) => ({ id: tabId })),
        id,
        beforeId,
      );
      access.save({ ...workspace, tabs: ordered.map((tab) => tab.id) });
    },
    [access],
  );

  useEffect(() => {
    // A successful load (including a retried read) is the initialization boundary.
    if (!loaded) return;
    const controller = new AbortController();
    async function discover() {
      try {
        const listed = new Set<string>();
        let initialPage: SessionIdPage | undefined;
        for await (const page of enumerateSessionPages(client, controller.signal)) {
          initialPage ??= page;
          for (const id of page.sessionIds) listed.add(id);
        }
        if (controller.signal.aborted || !initialPage) return;
        for (const id of listed) if (!isClosed(id)) discovered.current.add(id);
        setLastUsedSelection(initialPage.latestLlmSelection ?? null);
        const hash = hashSessionId();
        const candidates = [...Object.keys(access.read().chats), ...(hash ? [hash] : [])];
        forget(candidates.filter((id) => !listed.has(id)));
        if (hash && discovered.current.has(hash) && !isSnoozed(hash)) open(hash);
        else if (loaded!.first) {
          const tabs = initialPage.sessionIds.filter((id) => !isClosed(id));
          if (tabs.length) {
            access.save({
              ...access.read(),
              tabs,
              activeId: tabs.find((id) => !isSnoozed(id)) ?? null,
              chats: Object.fromEntries(tabs.map((id) => [id, newChat(null)])),
            });
          } else if (!hash && initialPage.nextCursor === null) await create();
        }
        if (!controller.signal.aborted) setReady(true);
      } catch (cause) {
        if (!controller.signal.aborted) setError(errorMessage(cause));
      }
    }
    void discover();
    return () => controller.abort();
  }, [client, loaded, access, forget, isClosed, isSnoozed, open, create]);

  useEffect(() => {
    if (ready) syncHash(state.activeId);
  }, [ready, state.activeId]);
  useEffect(() => {
    if (!ready) return;
    let checking: AbortController | null = null;
    async function hashChanged() {
      checking?.abort();
      const id = hashSessionId();
      if (!id || isClosed(id) || isSnoozed(id)) {
        syncHash(access.read().activeId);
        return;
      }
      if (access.read().activeId === id) return;
      const controller = new AbortController();
      checking = controller;
      try {
        const snapshot = await client.getSnapshot(id, controller.signal);
        if (controller.signal.aborted || hashSessionId() !== id) return;
        if (snapshot.sessionId === id && !isClosed(id)) open(id);
      } catch (cause) {
        if (controller.signal.aborted || hashSessionId() !== id) return;
        if (cause instanceof RequestError && cause.status === 404) forget([id]);
      }
      if (!controller.signal.aborted && hashSessionId() === id) syncHash(access.read().activeId);
    }
    function checkHash() {
      void hashChanged();
    }
    window.addEventListener("hashchange", checkHash);
    return () => {
      checking?.abort();
      window.removeEventListener("hashchange", checkHash);
    };
  }, [client, access, forget, isClosed, isSnoozed, open, ready]);

  return {
    ready,
    error,
    open,
    forget,
    reorder,
    discovered: discovered.current,
    lastUsedSelection,
  };
}
