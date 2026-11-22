import type { RefObject } from "preact";
import { useCallback, useEffect, useRef, useState } from "preact/hooks";
import type { SubagentLink } from "../../chat/subagents/SubagentNavigation.ts";
import { Popover } from "../../controls/Popover.tsx";
import { subagentPaths } from "./SubagentPaths.ts";
import "./subagent-menu.css";

interface MenuTarget {
  rootId: string;
  anchor: RefObject<HTMLButtonElement>;
  explicit: boolean;
}

export function useSubagentMenu() {
  const [target, setTarget] = useState<MenuTarget | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const stay = useCallback(() => clearTimeout(timer.current), []);
  const close = useCallback(() => {
    stay();
    setTarget(null);
  }, [stay]);

  useEffect(() => {
    const visibilityChanged = () => {
      if (document.hidden) close();
    };
    document.addEventListener("visibilitychange", visibilityChanged);
    return () => {
      document.removeEventListener("visibilitychange", visibilityChanged);
      stay();
    };
  }, [close, stay]);

  return {
    target,
    stay,
    close,
    hover(rootId: string, anchor: HTMLButtonElement, event: PointerEvent) {
      if (target?.explicit || event.pointerType === "touch" || event.buttons || document.hidden)
        return;
      stay();
      if (target?.rootId === rootId) return;
      timer.current = setTimeout(
        () => setTarget({ rootId, anchor: { current: anchor }, explicit: false }),
        150,
      );
    },
    open(rootId: string, anchor: HTMLButtonElement) {
      stay();
      setTarget({
        rootId,
        anchor: target?.rootId === rootId ? target.anchor : { current: anchor },
        explicit: true,
      });
    },
    leave() {
      if (target?.explicit) return;
      stay();
      if (target) timer.current = setTimeout(close, 250);
    },
  };
}

export function SubagentMenu({
  target: { rootId, anchor, explicit },
  title,
  load,
  onOpen,
  onStay,
  onLeave,
  onClose,
}: {
  target: MenuTarget;
  title: string;
  load: (rootId: string, signal: AbortSignal) => Promise<readonly SubagentLink[]>;
  onOpen: (rootId: string, path: readonly SubagentLink[]) => void;
  onStay: () => void;
  onLeave: () => void;
  onClose: () => void;
}) {
  const [paths, setPaths] = useState<SubagentLink[][]>([]);
  const [state, setState] = useState<"loading" | "loaded" | "failed">("loading");

  useEffect(() => {
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout>;
    let poll = explicit;
    const refresh = async () => {
      try {
        const links = await load(rootId, controller.signal);
        if (controller.signal.aborted) return;
        const nextPaths = subagentPaths(rootId, links);
        setPaths(nextPaths);
        setState("loaded");
        poll = explicit || nextPaths.length > 0;
      } catch {
        if (controller.signal.aborted) return;
        setState("failed");
      }
      if (poll) timer = setTimeout(() => void refresh(), 2000);
    };
    void refresh();
    return () => {
      controller.abort();
      clearTimeout(timer);
    };
  }, [rootId, load, explicit]);

  if (!explicit && !paths.length) return null;
  return (
    <Popover
      anchor={anchor}
      class="tab-subagents-popover"
      label={`Agents in ${title}`}
      autoFocus={explicit}
      onClose={onClose}
    >
      <div onPointerEnter={onStay} onPointerLeave={onLeave}>
        {paths.map((path) => (
          <button
            type="button"
            class="tab-subagent-entry"
            key={path.map((item) => item.id).join("/")}
            style={{ paddingLeft: `${10 + (path.length - 1) * 16}px` }}
            onClick={() => {
              onClose();
              onOpen(rootId, path);
            }}
          >
            {path.length > 1 && <span class="tab-subagent-elbow" aria-hidden="true" />}
            {path[path.length - 1]!.title}
          </button>
        ))}
        {paths.length === 0 && (
          <div class="tab-subagent-empty">
            {state === "loading"
              ? "Loading…"
              : state === "failed"
                ? "Agents unavailable"
                : "No agents"}
          </div>
        )}
      </div>
    </Popover>
  );
}
