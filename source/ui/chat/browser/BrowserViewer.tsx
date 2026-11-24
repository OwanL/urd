import { useEffect, useLayoutEffect, useRef, useState } from "preact/hooks";
import { IconButton } from "../../controls/IconButton.tsx";
import { Tooltip } from "../../controls/Tooltip.tsx";
import { pageTitle, readBrowserPages, type BrowserPage } from "./BrowserPage.ts";
import { BrowserIcon } from "./BrowserIcon.tsx";
import { BrowserIconButton } from "./BrowserIconButton.tsx";
import { BrowserPageView } from "./BrowserPageView.tsx";
import "./browser-viewer.css";

type BrowserViewerMode = "docked" | "floating" | "maximized" | "minimized";

interface BrowserViewerProps {
  sessionId: string;
  active: boolean;
  refresh: number;
  browserRefresh: number;
  openRequest: { ownerId: string; sequence: number } | null;
  onOwnersChange(owners: readonly string[]): void;
  onReturnToQuestion: (() => void) | undefined;
}

export function BrowserViewer({
  sessionId,
  active,
  refresh,
  browserRefresh,
  openRequest,
  onOwnersChange,
  onReturnToQuestion,
}: BrowserViewerProps) {
  const [pages, setPages] = useState<readonly BrowserPage[]>([]);
  const [readFailure, setReadFailure] = useState<string | null>(null);
  const [viewerOpen, setViewerOpen] = useState(false);
  const [mode, setMode] = useState<BrowserViewerMode>("docked");
  const [minimizedFrom, setMinimizedFrom] = useState<BrowserViewerMode>("docked");
  const [maximizedFrom, setMaximizedFrom] = useState<BrowserViewerMode>("docked");
  const [floatingPosition, setFloatingPosition] = useState<{
    left: number;
    top: number;
  } | null>(null);
  const [selectedPageId, setSelectedPageId] = useState<string | null>(null);
  const chip = useRef<HTMLButtonElement>(null);
  const panel = useRef<HTMLElement>(null);
  const tabButtons = useRef(new Map<string, HTMLButtonElement>());

  const ownersChanged = useRef(onOwnersChange);
  ownersChanged.current = onOwnersChange;
  useEffect(() => {
    if (!active) return;
    const controller = new AbortController();
    readBrowserPages(sessionId, controller.signal)
      .then((result) => {
        setPages(result);
        ownersChanged.current([
          ...new Set(result.filter((page) => page.active).map((page) => page.ownerId)),
        ]);
        setReadFailure(null);
        if (!result.length) setViewerOpen(false);
        setSelectedPageId((current) => {
          if (current && result.some((page) => page.id === current)) return current;
          return (result.find((page) => page.active) ?? result[0])?.id ?? null;
        });
      })
      .catch((failure) => {
        if (controller.signal.aborted) return;
        setReadFailure(failure instanceof Error ? failure.message : "Browser pages unavailable.");
      });
    return () => controller.abort();
  }, [sessionId, active, refresh, browserRefresh]);

  const handledRequest = useRef<number | null>(null);
  useEffect(() => {
    if (!openRequest || !active || handledRequest.current === openRequest.sequence) return;
    const page = pages.find((item) => item.ownerId === openRequest.ownerId && item.active);
    if (!page) return;
    handledRequest.current = openRequest.sequence;
    setSelectedPageId(page.id);
    setViewerOpen(true);
    setMode((current) => (current === "minimized" ? "docked" : current));
  }, [openRequest, pages, active]);

  const floating = mode === "floating" || (mode === "minimized" && minimizedFrom === "floating");
  const hasFloatingPosition = floatingPosition !== null;
  // A dragged position is viewport-relative and may no longer fit after a resize
  // or after restoring a floating panel from maximized mode.
  useLayoutEffect(() => {
    if (!floating || !viewerOpen || !hasFloatingPosition) return;
    const keepInViewport = () => {
      const bounds = panel.current?.getBoundingClientRect();
      if (!bounds) return;
      const maxLeft = Math.max(8, window.innerWidth - bounds.width - 8);
      const maxTop = Math.max(8, window.innerHeight - bounds.height - 8);
      setFloatingPosition((current) => {
        if (!current) return current;
        const left = Math.min(Math.max(current.left, 8), maxLeft);
        const top = Math.min(Math.max(current.top, 8), maxTop);
        return left === current.left && top === current.top ? current : { left, top };
      });
    };
    keepInViewport();
    window.addEventListener("resize", keepInViewport);
    return () => window.removeEventListener("resize", keepInViewport);
  }, [floating, viewerOpen, hasFloatingPosition]);

  const closeViewer = () => {
    setViewerOpen(false);
    chip.current?.focus({ preventScroll: true });
  };
  const maximize = () => {
    if (mode === "maximized") setMode(maximizedFrom);
    else if (mode === "minimized") setMode(minimizedFrom);
    else {
      setMaximizedFrom(mode);
      setMode("maximized");
    }
  };
  const minimize = () => {
    setMinimizedFrom(mode);
    setMode("minimized");
  };

  // Floating panels are dragged by the header; tabs and buttons stay clickable.
  const onHeaderPointerDown = (event: PointerEvent) => {
    if (event.button !== 0) return;
    if (event.target instanceof Element && event.target.closest("button")) return;
    const header = event.currentTarget as HTMLElement | null;
    const box = panel.current;
    if (!header || !box) return;
    const bounds = box.getBoundingClientRect();
    const grabX = event.clientX - bounds.left;
    const grabY = event.clientY - bounds.top;
    const leftLimit = Math.max(8, window.innerWidth - bounds.width - 8);
    const topLimit = Math.max(8, window.innerHeight - bounds.height - 8);
    header.setPointerCapture(event.pointerId);
    const move = (moveEvent: PointerEvent) => {
      setFloatingPosition({
        left: Math.min(Math.max(moveEvent.clientX - grabX, 8), leftLimit),
        top: Math.min(Math.max(moveEvent.clientY - grabY, 8), topLimit),
      });
    };
    const end = () => {
      header.removeEventListener("pointermove", move);
      header.removeEventListener("pointerup", end);
      header.removeEventListener("pointercancel", end);
    };
    header.addEventListener("pointermove", move);
    header.addEventListener("pointerup", end);
    header.addEventListener("pointercancel", end);
  };

  const onKeyDownTabs = (event: KeyboardEvent) => {
    if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key) || !pages.length) return;
    event.preventDefault();
    const lastIndex = pages.length - 1;
    const index = pages.findIndex((page) => page.id === selectedPageId);
    const next =
      event.key === "Home"
        ? 0
        : event.key === "End"
          ? lastIndex
          : event.key === "ArrowLeft"
            ? index < 1
              ? lastIndex
              : index - 1
            : index < 0 || index === lastIndex
              ? 0
              : index + 1;
    const page = pages[next];
    if (!page) return;
    setSelectedPageId(page.id);
    tabButtons.current.get(page.id)?.focus();
  };

  const selectedPage =
    pages.find((page) => page.id === selectedPageId) ??
    pages.find((page) => page.active) ??
    pages[0];
  if (!pages.length && !viewerOpen) return null;
  const panelClass = `browser-viewer-panel${mode === "minimized" ? " is-minimized" : ` is-${mode}`}${floating ? " is-floating" : ""}`;

  return (
    <>
      {pages.length > 0 && (
        <Tooltip
          content={pages.map((page) => (
            <span class="tooltip-detail" key={page.id}>
              {pageTitle(page)}
            </span>
          ))}
        >
          <button
            ref={chip}
            type="button"
            class={`browser-viewer-chip${viewerOpen ? " is-open" : ""}`}
            aria-label={`Browser pages (${pages.length})`}
            aria-expanded={viewerOpen}
            onClick={() => setViewerOpen(!viewerOpen)}
          >
            <BrowserIcon name="browser" />
            <span aria-hidden="true">{pages.length}</span>
          </button>
        </Tooltip>
      )}
      {viewerOpen && (
        <section
          ref={panel}
          class={panelClass}
          aria-label="Browser pages"
          style={
            floating && floatingPosition
              ? {
                  left: `${floatingPosition.left}px`,
                  top: `${floatingPosition.top}px`,
                  right: "auto",
                  bottom: "auto",
                }
              : undefined
          }
        >
          <header
            class="browser-viewer-header"
            onPointerDown={floating ? onHeaderPointerDown : undefined}
          >
            <nav
              class="browser-viewer-tabs"
              role="tablist"
              aria-label="Browser pages"
              onKeyDown={onKeyDownTabs}
            >
              {pages.map((page) => (
                <Tooltip key={page.id} content={<span class="tooltip-detail">{page.url}</span>}>
                  <button
                    ref={(element) => {
                      if (element) tabButtons.current.set(page.id, element);
                      else tabButtons.current.delete(page.id);
                    }}
                    type="button"
                    id={`browser-tab-${page.id}`}
                    role="tab"
                    aria-selected={page.id === selectedPage?.id}
                    tabindex={page.id === selectedPage?.id ? 0 : -1}
                    class="browser-viewer-tab"
                    onClick={() => setSelectedPageId(page.id)}
                  >
                    {pageTitle(page)}
                  </button>
                </Tooltip>
              ))}
            </nav>
            <div class="browser-viewer-controls">
              {openRequest && onReturnToQuestion && (
                <button
                  type="button"
                  class="browser-viewer-return"
                  onClick={() => {
                    setViewerOpen(false);
                    onReturnToQuestion();
                  }}
                >
                  Question
                </button>
              )}
              {mode === "minimized" ? (
                <BrowserIconButton
                  icon="maximize"
                  label="Restore browser viewer"
                  onClick={() => setMode(minimizedFrom)}
                />
              ) : (
                <BrowserIconButton icon="minimize" label="Minimize viewer" onClick={minimize} />
              )}
              {mode !== "minimized" && mode !== "docked" && (
                <BrowserIconButton
                  icon="dock"
                  label="Dock viewer"
                  onClick={() => setMode("docked")}
                />
              )}
              {mode !== "minimized" && mode !== "floating" && (
                <BrowserIconButton
                  icon="float"
                  label="Float viewer"
                  onClick={() => setMode("floating")}
                />
              )}
              {mode !== "minimized" && (
                <BrowserIconButton
                  icon={mode === "maximized" ? "restore" : "maximize"}
                  label={mode === "maximized" ? "Restore viewer size" : "Maximize viewer"}
                  onClick={maximize}
                />
              )}
              <IconButton icon="close" label="Close browser viewer" onClick={closeViewer} />
            </div>
          </header>
          {mode !== "minimized" && (
            <div
              class="browser-viewer-body"
              role="tabpanel"
              aria-labelledby={selectedPage ? `browser-tab-${selectedPage.id}` : undefined}
            >
              {readFailure && (
                <p class="browser-viewer-notice" role="status">
                  {readFailure}
                </p>
              )}
              {selectedPage && (
                <BrowserPageView sessionId={sessionId} page={selectedPage} enabled={active} />
              )}
            </div>
          )}
        </section>
      )}
    </>
  );
}
