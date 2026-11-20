import type { RefObject } from "preact";
import { useEffect } from "preact/hooks";
import type { ActiveTarget } from "./ToolRowInteractionTypes.ts";

export function useToolAnchorVisibility(
  active: ActiveTarget | null,
  activeRef: RefObject<ActiveTarget | null>,
  root: HTMLElement | null,
  dismiss: (returnFocus: boolean) => void,
) {
  const activeId = active?.id;
  useEffect(() => {
    const currentTarget = activeRef.current;
    if (!currentTarget || currentTarget.id !== activeId) return;
    const targetId = currentTarget.id;
    const anchor = currentTarget.anchor;
    function outsideRoot() {
      if (activeRef.current?.id !== targetId || !anchor.isConnected) {
        dismiss(false);
        return;
      }
      if (!root) return;
      const bounds = root.getBoundingClientRect();
      const rect = anchor.getBoundingClientRect();
      if (rect.bottom <= bounds.top || rect.top >= bounds.bottom) dismiss(false);
    }
    const observer =
      typeof IntersectionObserver === "undefined"
        ? null
        : new IntersectionObserver(
            ([entry]) => {
              if (entry && !entry.isIntersecting) dismiss(false);
            },
            { root },
          );
    observer?.observe(anchor);
    root?.addEventListener("scroll", outsideRoot, { passive: true });
    window.addEventListener("scroll", outsideRoot, true);
    window.addEventListener("resize", outsideRoot);
    return () => {
      observer?.disconnect();
      root?.removeEventListener("scroll", outsideRoot);
      window.removeEventListener("scroll", outsideRoot, true);
      window.removeEventListener("resize", outsideRoot);
    };
    // The ref checks the latest target without resubscribing on changes to its contents.
    // oxlint-disable-next-line react-hooks/exhaustive-deps
  }, [activeId, activeRef, dismiss, root]);
}
