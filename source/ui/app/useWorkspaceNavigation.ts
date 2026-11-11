import { useCallback, useEffect, useRef, useState } from "preact/hooks";
import type { SessionClient } from "../sessions/SessionClient.ts";
import { moveTab } from "./TabOrder.ts";
import { newChat } from "./WorkspaceState.ts";
import type { SessionIdPage } from "../sessions/SessionIdPage.ts";
import type { PersistedWorkspace } from "./usePersistedWorkspace.ts";
import { enumerateSessionPages } from "./enumerateSessionPages.ts";

const idPattern = /^[\da-f]{8}(-[\da-f]{4}){3}-[\da-f]{12}$/i;

// Navigation changes the local workspace, never the lifetime of backend work.
export function useWorkspaceNavigation(
  client: SessionClient,
  { state, loaded, access }: Pick<PersistedWorkspace, "state" | "loaded" | "access">,
  create: () => Promise<void>,
) {
  const [ready, setReady] = useState(false);
  const discovered = useRef(new Set<string>());
  const [error, setError] = useState<string | null>(null);
  const open = useCallback(
    (id: string) => {
      const workspace = access.read();
      access.save({
        ...workspace,
        activeId: id,
        tabs: workspace.tabs.includes(id) ? workspace.tabs : [...workspace.tabs, id],
        chats: { ...workspace.chats, [id]: workspace.chats[id] ?? newChat(null) },
      });
    },
    [access],
  );
  const close = useCallback(
    (id: string) => {
      const workspace = access.read();
      const index = workspace.tabs.indexOf(id);
      const tabs = workspace.tabs.filter((tab) => tab !== id);
      access.save({
        ...workspace,
        tabs,
        activeId:
          workspace.activeId === id
            ? (tabs[Math.min(index, tabs.length - 1)] ?? null)
            : workspace.activeId,
      });
    },
    [access],
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
    // Its identity stays stable through later edits and storage-write retries.
    if (!loaded) return;
    const controller = new AbortController();
    async function discover() {
      try {
        let initialPage: SessionIdPage | undefined;
        for await (const page of enumerateSessionPages(client, controller.signal)) {
          initialPage ??= page;
          for (const id of page.sessionIds) discovered.current.add(id);
        }
        if (controller.signal.aborted || !initialPage) return;
        const hash = window.location.hash.slice(1);
        if (idPattern.test(hash)) open(hash);
        else if (loaded!.first) {
          if (initialPage.sessionIds.length > 0) {
            const workspace = access.read();
            access.save({
              ...workspace,
              tabs: [...initialPage.sessionIds],
              activeId: initialPage.sessionIds[0] ?? null,
              chats: Object.fromEntries(initialPage.sessionIds.map((id) => [id, newChat(null)])),
            });
          } else if (initialPage.nextCursor === null) await create();
        }
        if (!controller.signal.aborted) setReady(true);
      } catch (cause) {
        if (!controller.signal.aborted)
          setError(cause instanceof Error ? cause.message : String(cause));
      }
    }
    void discover();
    return () => controller.abort();
  }, [client, loaded, access, open, create]);

  useEffect(() => {
    if (!ready) return;
    window.history.replaceState(
      null,
      "",
      `${window.location.pathname}${window.location.search}${state.activeId ? `#${state.activeId}` : ""}`,
    );
  }, [ready, state.activeId]);
  useEffect(() => {
    function hashChanged() {
      const id = window.location.hash.slice(1);
      if (idPattern.test(id)) open(id);
    }
    window.addEventListener("hashchange", hashChanged);
    return () => window.removeEventListener("hashchange", hashChanged);
  }, [open]);

  return { ready, error, open, close, reorder, discovered: discovered.current };
}
