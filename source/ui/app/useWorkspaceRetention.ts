import { useRef } from "preact/hooks";

// Workspace-wide focus and selection can pin a retained conversation, including its composer.
export function useWorkspaceRetention(
  activeId: string | null,
  tabIds: readonly string[],
  retainedTabLimit: number | null,
): readonly string[] {
  const retained = useRef<string[]>([]);
  if (retainedTabLimit === null) return activeId ? [activeId] : [];

  if (activeId) {
    const selection = document.getSelection();
    const pinned = new Set(
      [selection?.anchorNode, selection?.focusNode, document.activeElement].flatMap((node) => {
        const element = node instanceof Element ? node : node?.parentElement;
        const id = element?.closest<HTMLElement>("[data-chat-session]")?.dataset.chatSession;
        return id ? [id] : [];
      }),
    );
    const ordered = [
      activeId,
      ...retained.current.filter((id) => id !== activeId && tabIds.includes(id)),
    ];
    retained.current = ordered.filter((id, index) => index < retainedTabLimit || pinned.has(id));
  }
  return retained.current.filter((id) => tabIds.includes(id));
}
