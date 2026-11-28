import type { ComponentChildren } from "preact";
import { useEffect, useId, useLayoutEffect, useRef, useState } from "preact/hooks";
import { Tooltip } from "../../controls/Tooltip.tsx";
import { pageTitle, readBrowserPages, type BrowserPage } from "./BrowserPage.ts";
import { BrowserIcon } from "./BrowserIcon.tsx";
import { BrowserIconButton } from "./BrowserIconButton.tsx";
import { BrowserPageView } from "./BrowserPageView.tsx";
import { BrowserWindow } from "./BrowserWindow.tsx";
import { useBitwardenPage } from "./useBitwardenPage.ts";
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
  const [bitwardenAvailable, setBitwardenAvailable] = useState(false);
  const [readFailure, setReadFailure] = useState<string | null>(null);
  const [open, setOpen] = useState(false);
  const [fromQuestion, setFromQuestion] = useState(false);
  const [selectedPageId, setSelectedPageId] = useState<string | null>(null);
  const trigger = useRef<HTMLButtonElement>(null);
  const returnFocus = useRef<(() => void) | null>(null);
  const panelId = useId();
  const hasPages = pages.length > 0;
  const viewerOpen = active && open && hasPages;
  const {
    state: bitwarden,
    close: closeBitwarden,
    toggle: toggleBitwarden,
  } = useBitwardenPage(sessionId, viewerOpen && bitwardenAvailable);

  useEffect(() => {
    if (!active) return;
    const controller = new AbortController();
    readBrowserPages(sessionId, controller.signal)
      .then((result) => {
        if (controller.signal.aborted) return;
        const nextPages = result.pages ?? [];
        setPages(nextPages);
        setBitwardenAvailable(result.bitwarden === true);
        onOwnersChange([
          ...new Set(nextPages.filter((page) => page.active).map((page) => page.ownerId)),
        ]);
        setReadFailure(null);
        if (!nextPages.length) setOpen(false);
        setSelectedPageId((current) => {
          if (nextPages.some((page) => page.id === current)) return current;
          return (nextPages.find((page) => page.active) ?? nextPages[0])?.id ?? null;
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
    closeBitwarden();
    setSelectedPageId(page.id);
    setFromQuestion(true);
    setOpen(true);
  }, [openRequest, pages, active, closeBitwarden]);

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
  const showingBitwarden = bitwarden?.kind === "open";
  const displayed = showingBitwarden ? bitwarden.page : selected;
  const bitwardenNotice =
    bitwarden?.kind === "opening"
      ? "Opening Bitwarden…"
      : bitwarden?.kind === "failed"
        ? bitwarden.message
        : null;
  const launcher = active && hasPages && (
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
        open={viewerOpen}
        pages={pages}
        selected={selected}
        showingBitwarden={showingBitwarden}
        bitwardenControl={
          bitwardenAvailable ? (
            <BrowserIconButton
              icon="key"
              label={
                showingBitwarden
                  ? "Close Bitwarden"
                  : bitwarden?.kind === "opening"
                    ? "Cancel opening Bitwarden"
                    : "Open Bitwarden"
              }
              aria-expanded={showingBitwarden}
              aria-controls={`${panelId}-page`}
              class="browser-bitwarden-control"
              onClick={toggleBitwarden}
            />
          ) : undefined
        }
        onSelect={(id) => {
          closeBitwarden();
          setSelectedPageId(id);
        }}
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
        {bitwardenNotice && (
          <p class="browser-viewer-notice" role="status">
            {bitwardenNotice}
          </p>
        )}
        {displayed && (
          <BrowserPageView
            key={displayed.id}
            sessionId={sessionId}
            page={displayed}
            enabled={active}
          />
        )}
      </BrowserWindow>
    </>
  );
}
