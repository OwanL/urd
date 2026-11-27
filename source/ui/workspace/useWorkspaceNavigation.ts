import { useCallback, useEffect, useState } from "preact/hooks";
import type { LlmSelection } from "../providers/LlmSelection.ts";
import type { SessionClient } from "../sessions/SessionClient.ts";
import { RequestError } from "../http/RequestError.ts";
import { errorMessage } from "../http/ErrorMessages.ts";
import { moveTab } from "./tabs/TabOrder.ts";
import { mergeWorkspaceSessions } from "./WorkspaceState.ts";
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
  const [error, setError] = useState<string | null>(null);
  const forget = useCallback(
    (ids: Iterable<string>) => {
      const removed = new Set([...ids].filter((id) => !isClosed(id)));
      if (removed.size) forgetSessions(removed);
    },
    [forgetSessions, isClosed],
  );
  const open = useCallback(
    (id: string) => {
      if (isClosed(id) || isSnoozed(id)) return;
      const workspace = access.read();
      if (workspace.activeId === id) return;
      access.save({ ...mergeWorkspaceSessions(workspace, [id]), activeId: id });
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
    const { first } = loaded;
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
        setLastUsedSelection(initialPage.latestLlmSelection ?? null);
        forget(Object.keys(access.read().chats).filter((id) => !listed.has(id)));

        const previous = access.read();
        const workspace = mergeWorkspaceSessions(
          previous,
          [...listed].filter((id) => !isClosed(id)),
        );
        const hash = hashSessionId();
        const activeId =
          hash && listed.has(hash) && !isClosed(hash) && !isSnoozed(hash)
            ? hash
            : (workspace.activeId ?? workspace.tabs.find((id) => !isSnoozed(id)) ?? null);
        if (workspace !== previous || activeId !== previous.activeId)
          access.save({ ...workspace, activeId });
        else if (first && !hash && !listed.size) await create();
        if (!controller.signal.aborted) setReady(true);
      } catch (cause) {
        if (!controller.signal.aborted) setError(errorMessage(cause));
      }
    }
    void discover();
    return () => controller.abort();
  }, [client, loaded, access, forget, isClosed, isSnoozed, create]);

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
    lastUsedSelection,
  };
}
