import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "preact/hooks";
import type { SubagentLink } from "../../chat/subagents/SubagentNavigation.ts";
import { Icon } from "../../controls/Icon.tsx";
import { Popover } from "../../controls/Popover.tsx";
import { Tooltip } from "../../controls/Tooltip.tsx";
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
  onOpen: (rootId: string, path: readonly SubagentLink[], opener: HTMLButtonElement | null) => void;
}) {
  const anchor = useRef<HTMLButtonElement>(null);
  const [open, setOpen] = useState(false);
  const [focused, setFocused] = useState(false);
  const [paths, setPaths] = useState<SubagentLink[][]>([]);
  const [failed, setFailed] = useState(false);
  const [loaded, setLoaded] = useState(false);
  const [pageVisible, setPageVisible] = useState(() => !document.hidden);

  const close = useCallback(() => {
    setOpen(false);
    setPaths([]);
    setFailed(false);
    setLoaded(false);
  }, []);

  useLayoutEffect(() => {
    if (!busy && !open && focused && document.activeElement === anchor.current)
      document.getElementById(`tab-${rootId}`)?.focus({ preventScroll: true });
  }, [busy, open, focused, rootId]);

  useEffect(() => {
    if (!open || !pageVisible || document.hidden || dragging) return;
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    const publish = (next: SubagentLink[][], unavailable: boolean) => {
      const item = document.activeElement;
      const key = item instanceof HTMLElement ? item.dataset.agentPath : undefined;
      const surface = item?.closest<HTMLElement>(".tab-subagents-popover");
      if (
        key &&
        surface?.parentElement?.contains(anchor.current) &&
        !next.some((p) => pathKey(p) === key)
      )
        surface.focus({ preventScroll: true });
      setPaths(next);
      setFailed(unavailable);
      setLoaded(true);
    };
    const refresh = async () => {
      try {
        const result = await load(rootId, controller.signal);
        if (controller.signal.aborted) return;
        publish(subagentPaths(rootId, result), false);
      } catch {
        if (controller.signal.aborted) return;
        publish([], true);
      }
      if (busy) timer = setTimeout(() => void refresh(), 2000);
    };
    void refresh();
    return () => {
      controller.abort();
      clearTimeout(timer);
    };
  }, [open, pageVisible, dragging, busy, rootId, load]);

  useEffect(() => {
    if (!open) return;
    const visibilityChanged = () => setPageVisible(!document.hidden);
    document.addEventListener("visibilitychange", visibilityChanged);
    visibilityChanged();
    return () => document.removeEventListener("visibilitychange", visibilityChanged);
  }, [open]);

  useEffect(() => {
    if (dragging && open) close();
  }, [dragging, open, close]);

  if (!busy && !open && !focused) return null;

  return (
    <span class="tab-subagents">
      <Tooltip content="Live agents" placement="below" disabled={open || dragging}>
        <button
          ref={anchor}
          type="button"
          class="tab-subagents-button"
          aria-label={`Agents in ${title}`}
          aria-haspopup="dialog"
          aria-expanded={open}
          onFocus={() => setFocused(true)}
          onBlur={() => setFocused(false)}
          onClick={() => (open ? close() : setOpen(true))}
        >
          <Icon name="subagents" />
        </button>
      </Tooltip>
      {open && (
        <Popover
          anchor={anchor}
          class="tab-subagents-popover"
          label={`Agents in ${title}`}
          onClose={close}
        >
          <div class="tab-subagents-heading">Live agents</div>
          {paths.map((path) => (
            <button
              type="button"
              class="tab-subagent-entry"
              key={pathKey(path)}
              data-agent-path={pathKey(path)}
              style={{ paddingLeft: `${10 + Math.min(path.length - 1, 3) * 16}px` }}
              aria-label={path.map((item) => item.title).join(" / ")}
              onClick={() => {
                close();
                onOpen(rootId, path, anchor.current);
              }}
            >
              {path.length > 1 && <span class="tab-subagent-elbow" aria-hidden="true" />}
              <span>{path[path.length - 1]!.title}</span>
            </button>
          ))}
          {paths.length === 0 && (
            <div class="tab-subagent-empty" role="status">
              {!loaded ? "Loading…" : failed ? "Agents unavailable" : "No live agents"}
            </div>
          )}
        </Popover>
      )}
    </span>
  );
}

function pathKey(path: readonly SubagentLink[]): string {
  return path.map((item) => item.id).join("/");
}
