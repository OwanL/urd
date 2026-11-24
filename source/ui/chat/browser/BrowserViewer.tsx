import type { ComponentChildren } from "preact";
import { useEffect, useId, useLayoutEffect, useRef, useState } from "preact/hooks";
import { Tooltip } from "../../controls/Tooltip.tsx";
import { pageTitle, readBrowserPages, type BrowserPage } from "./BrowserPage.ts";
import { BrowserIcon } from "./BrowserIcon.tsx";
import { BrowserPageView } from "./BrowserPageView.tsx";
import { BrowserWindow } from "./BrowserWindow.tsx";
import "./browser-viewer.css";

interface BrowserViewerProps {
  sessionId: string;
  active: boolean;
  refresh: number;
  browserRefresh: number;
  openRequest: { ownerId: string; sequence: number } | null;
  onOwnersChange(owners: readonly string[]): void;
  onReturnToQuestion: (() => void) | undefined;
  children(launcher: ComponentChildren): ComponentChildren;
}

export function BrowserViewer({
  sessionId,
  active,
  refresh,
  browserRefresh,
  openRequest,
  onOwnersChange,
  onReturnToQuestion,
  children,
}: BrowserViewerProps) {
  const [pages, setPages] = useState<readonly BrowserPage[]>([]);
  const [readFailure, setReadFailure] = useState<string | null>(null);
  const [open, setOpen] = useState(false);
  const [fromQuestion, setFromQuestion] = useState(false);
  const [selectedPageId, setSelectedPageId] = useState<string | null>(null);
  const trigger = useRef<HTMLButtonElement>(null);
  const returnFocus = useRef<(() => void) | null>(null);
  const panelId = useId();

  useEffect(() => {
    if (!active) return;
    const controller = new AbortController();
    readBrowserPages(sessionId, controller.signal)
      .then((result) => {
        if (controller.signal.aborted) return;
        setPages(result);
        onOwnersChange([
          ...new Set(result.filter((page) => page.active).map((page) => page.ownerId)),
        ]);
        setReadFailure(null);
        if (!result.length) setOpen(false);
        setSelectedPageId((current) => {
          if (result.some((page) => page.id === current)) return current;
          return (result.find((page) => page.active) ?? result[0])?.id ?? null;
        });
      })
      .catch((failure) => {
        if (!controller.signal.aborted)
          setReadFailure(failure instanceof Error ? failure.message : "Browser pages unavailable.");
      });
    return () => controller.abort();
  }, [sessionId, active, refresh, browserRefresh, onOwnersChange]);

  const handledRequest = useRef<number | null>(null);
  useEffect(() => {
    if (!openRequest || !active || handledRequest.current === openRequest.sequence) return;
    const page = pages.find((item) => item.ownerId === openRequest.ownerId && item.active);
    if (!page) return;
    handledRequest.current = openRequest.sequence;
    setSelectedPageId(page.id);
    setFromQuestion(true);
    setOpen(true);
  }, [openRequest, pages, active]);

  useLayoutEffect(() => {
    if (open || !returnFocus.current) return;
    returnFocus.current();
    returnFocus.current = null;
  }, [open]);

  const minimize = (focus = () => trigger.current?.focus({ preventScroll: true })) => {
    returnFocus.current = focus;
    setOpen(false);
  };
  const selected = pages.find((page) => page.id === selectedPageId) ?? pages[0];
  const launcher = pages.length > 0 && (
    <Tooltip
      content={
        <>
          {open ? "Minimize browser" : "Join browser"}
          {pages.map((page) => (
            <span class="tooltip-detail" key={page.id}>
              {pageTitle(page)}
            </span>
          ))}
        </>
      }
    >
      <button
        ref={trigger}
        type="button"
        class="browser-launcher"
        aria-label={open ? "Minimize browser" : "Join browser"}
        aria-expanded={open}
        aria-controls={open ? panelId : undefined}
        onClick={() => {
          if (open) minimize();
          else {
            setFromQuestion(false);
            setOpen(true);
          }
        }}
      >
        <BrowserIcon name="browser" />
      </button>
    </Tooltip>
  );

  return (
    <>
      <div class="chat-pane">{children(launcher)}</div>
      <BrowserWindow
        id={panelId}
        open={active && open && !!selected}
        pages={pages}
        selected={selected}
        onSelect={setSelectedPageId}
        onMinimize={() => minimize()}
        onQuestion={
          fromQuestion && onReturnToQuestion ? () => minimize(onReturnToQuestion) : undefined
        }
      >
        {readFailure && (
          <p class="browser-viewer-notice" role="status">
            {readFailure}
          </p>
        )}
        {selected && <BrowserPageView sessionId={sessionId} page={selected} enabled={active} />}
      </BrowserWindow>
    </>
  );
}
