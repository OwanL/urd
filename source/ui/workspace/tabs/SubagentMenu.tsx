import { useCallback, useEffect, useRef, useState } from "preact/hooks";
import type { SubagentLink } from "../../chat/subagents/SubagentNavigation.ts";
import { Icon } from "../../controls/Icon.tsx";
import { Popover } from "../../controls/Popover.tsx";
import { subagentPaths } from "./SubagentPaths.ts";
import "./subagent-menu.css";

export function SubagentMenu({
  rootId,
  title,
  busy,
  dragging,
  load,
  onOpen,
}: {
  rootId: string;
  title: string;
  busy: boolean;
  dragging: boolean;
  load: (rootId: string, signal: AbortSignal) => Promise<readonly SubagentLink[]>;
  onOpen: (rootId: string, path: readonly SubagentLink[]) => void;
}) {
  const anchor = useRef<HTMLButtonElement>(null);
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const [requested, setRequested] = useState(false);
  const [explicit, setExplicit] = useState(false);
  const [links, setLinks] = useState<readonly SubagentLink[]>([]);
  const [failed, setFailed] = useState(false);
  const [loaded, setLoaded] = useState(false);
  const [pageVisible, setPageVisible] = useState(() => !document.hidden);
  const paths = subagentPaths(rootId, links);
  const visible = requested && (explicit || paths.length > 0);

  useEffect(() => {
    if (!requested || !pageVisible || dragging) return;
    const controller = new AbortController();
    void load(rootId, controller.signal).then(
      (result) => {
        if (controller.signal.aborted) return;
        setLinks(result);
        setFailed(false);
        setLoaded(true);
      },
      () => {
        if (controller.signal.aborted) return;
        setFailed(true);
        setLoaded(true);
      },
    );
    return () => controller.abort();
  }, [requested, pageVisible, dragging, rootId, load]);

  useEffect(() => {
    if (!visible || !busy || !loaded || !pageVisible || dragging) return;
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout>;
    const refresh = async () => {
      try {
        const result = await load(rootId, controller.signal);
        if (!controller.signal.aborted) {
          setLinks(result);
          setFailed(false);
        }
      } catch {
        if (!controller.signal.aborted) setFailed(true);
      }
      if (!controller.signal.aborted) timer = setTimeout(() => void refresh(), 2000);
    };
    timer = setTimeout(() => void refresh(), 2000);
    return () => {
      controller.abort();
      clearTimeout(timer);
    };
  }, [visible, busy, loaded, pageVisible, dragging, rootId, load]);

  useEffect(() => () => clearTimeout(timer.current), []);
  const close = useCallback(() => {
    clearTimeout(timer.current);
    setRequested(false);
    setExplicit(false);
    setLinks([]);
    setFailed(false);
    setLoaded(false);
  }, []);
  const leave = useCallback(() => {
    if (explicit) return;
    clearTimeout(timer.current);
    timer.current = setTimeout(close, 250);
  }, [explicit, close]);
  const enter = () => {
    clearTimeout(timer.current);
  };

  useEffect(() => {
    const visibilityChanged = () => {
      setPageVisible(!document.hidden);
      if (document.hidden) close();
    };
    document.addEventListener("visibilitychange", visibilityChanged);
    return () => document.removeEventListener("visibilitychange", visibilityChanged);
  }, [close]);

  useEffect(() => {
    if (dragging) close();
  }, [dragging, close]);

  useEffect(() => {
    const titleButton = anchor.current?.closest(".chat-tab")?.querySelector('[role="tab"]');
    if (!titleButton) return;
    const hover = (event: Event) => {
      const pointer = event as PointerEvent;
      if (pointer.pointerType === "touch" || pointer.buttons || document.hidden || dragging) return;
      enter();
      setRequested(true);
    };
    titleButton.addEventListener("pointerenter", hover);
    titleButton.addEventListener("pointerleave", leave);
    titleButton.addEventListener("pointerdown", close);
    return () => {
      titleButton.removeEventListener("pointerenter", hover);
      titleButton.removeEventListener("pointerleave", leave);
      titleButton.removeEventListener("pointerdown", close);
    };
  }, [dragging, leave, close]);

  return (
    <span
      class="tab-subagents"
      onPointerEnter={(event) => {
        if (event.pointerType === "touch" || event.buttons || document.hidden || dragging) return;
        enter();
        setRequested(true);
      }}
      onPointerLeave={leave}
    >
      <button
        ref={anchor}
        type="button"
        class="tab-subagents-button"
        aria-label={`Agents in ${title}`}
        aria-haspopup="dialog"
        aria-expanded={visible}
        onClick={() => {
          if (explicit) close();
          else {
            enter();
            setExplicit(true);
            setRequested(true);
          }
        }}
      >
        <Icon name="chevron" />
      </button>
      {visible && (
        <Popover
          anchor={anchor}
          class="tab-subagents-popover"
          label={`Agents in ${title}`}
          autoFocus={explicit}
          onClose={close}
        >
          <div onPointerEnter={enter} onPointerLeave={leave}>
            {paths.map((path) => (
              <button
                type="button"
                class="tab-subagent-entry"
                key={path.map((item) => item.id).join("/")}
                style={{ paddingLeft: `${10 + (path.length - 1) * 16}px` }}
                onClick={() => {
                  close();
                  onOpen(rootId, path);
                }}
              >
                {path.length > 1 && <span class="tab-subagent-elbow" aria-hidden="true" />}
                {path[path.length - 1]!.title}
              </button>
            ))}
            {explicit && paths.length === 0 && (
              <div class="tab-subagent-empty">
                {!loaded ? "Loading…" : failed ? "Agents unavailable" : "No agents"}
              </div>
            )}
          </div>
        </Popover>
      )}
    </span>
  );
}
