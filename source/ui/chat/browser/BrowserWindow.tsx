import type { ComponentChildren } from "preact";
import { useLayoutEffect } from "preact/hooks";
import { Tooltip } from "../../controls/Tooltip.tsx";
import { BrowserIcon } from "./BrowserIcon.tsx";
import { BrowserIconButton } from "./BrowserIconButton.tsx";
import { pageTitle, type BrowserPage } from "./BrowserPage.ts";
import {
  dividerEdge,
  useBrowserWindow,
  type BrowserWindowMode,
  type ResizeEdge,
} from "./useBrowserWindow.ts";

interface BrowserWindowProps {
  id: string;
  open: boolean;
  pages: readonly BrowserPage[];
  selected: BrowserPage | undefined;
  showingBitwarden: boolean;
  bitwardenControl: ComponentChildren;
  onSelect(id: string): void;
  onMinimize(): void;
  onQuestion: (() => void) | undefined;
  children: ComponentChildren;
}

const edges: ResizeEdge[] = ["n", "e", "s", "w", "ne", "se", "sw", "nw"];
const placements: Record<BrowserWindowMode, string> = {
  floating: "Floating",
  left: "Left half",
  right: "Right half",
  top: "Top half",
  bottom: "Bottom half",
  maximized: "Maximized",
};

export function BrowserWindow({
  id,
  open,
  pages,
  selected,
  showingBitwarden,
  bitwardenControl,
  onSelect,
  onMinimize,
  onQuestion,
  children,
}: BrowserWindowProps) {
  const {
    panel,
    mode,
    rect,
    split,
    place,
    preview,
    onHeaderPointerDown,
    onResizePointerDown,
    onDividerKeyDown,
    pointerEvents,
  } = useBrowserWindow(open);
  useLayoutEffect(() => {
    if (!open) return;
    const surface = panel.current?.querySelector<HTMLElement>(".browser-page-surface");
    const bitwarden = panel.current?.querySelector<HTMLElement>(".browser-bitwarden-control");
    (surface ?? bitwarden)?.focus({ preventScroll: true });
  }, [open, panel]);
  if (!open) return null;
  const showTabs = pages.length > 1 || (!!bitwardenControl && pages.length > 0);
  const docked = mode !== "floating" && mode !== "maximized";

  const moveTabFocus = (event: KeyboardEvent) => {
    if (!selected) return;
    const index = pages.indexOf(selected);
    const next =
      event.key === "Home"
        ? 0
        : event.key === "End"
          ? pages.length - 1
          : event.key === "ArrowRight"
            ? (index + 1) % pages.length
            : event.key === "ArrowLeft"
              ? (index + pages.length - 1) % pages.length
              : null;
    if (next === null) return;
    event.preventDefault();
    const page = pages[next]!;
    onSelect(page.id);
    document.getElementById(`${id}-${page.id}`)?.focus();
  };
  return (
    <>
      <section
        id={id}
        ref={panel}
        {...pointerEvents}
        class="browser-viewer-panel"
        data-mode={mode}
        aria-label="Browser window"
        style={
          rect
            ? {
                left: `${rect.x}px`,
                top: `${rect.y}px`,
                width: `${rect.width}px`,
                height: `${rect.height}px`,
              }
            : docked
              ? { flexBasis: `${split}%` }
              : undefined
        }
      >
        <header class="browser-viewer-header" onPointerDown={onHeaderPointerDown}>
          {showTabs ? (
            <nav
              class="browser-viewer-tabs"
              role="tablist"
              aria-label="Browser pages"
              onKeyDown={moveTabFocus}
            >
              {pages.map((page) => (
                <Tooltip key={page.id} content={page.url}>
                  <button
                    type="button"
                    role="tab"
                    id={`${id}-${page.id}`}
                    aria-controls={`${id}-page`}
                    aria-selected={!showingBitwarden && page.id === selected?.id}
                    tabindex={page.id === selected?.id ? 0 : -1}
                    class="browser-viewer-tab"
                    onClick={() => onSelect(page.id)}
                  >
                    {pageTitle(page)}
                  </button>
                </Tooltip>
              ))}
            </nav>
          ) : (
            <span class="browser-viewer-title" title={selected?.url}>
              {selected ? pageTitle(selected) : showingBitwarden ? "Bitwarden" : "Browser"}
            </span>
          )}
          <div class="browser-viewer-grip" aria-hidden="true" />
          {onQuestion && (
            <button type="button" class="browser-viewer-return" onClick={onQuestion}>
              Question
            </button>
          )}
          {bitwardenControl}
          <label class="browser-layout-control" title="Window layout">
            <BrowserIcon name="layout" />
            <select
              aria-label="Window layout"
              value={mode}
              onChange={(event) => place(event.currentTarget.value as BrowserWindowMode)}
            >
              {Object.entries(placements).map(([value, label]) => (
                <option key={value} value={value}>
                  {label}
                </option>
              ))}
            </select>
          </label>
          <BrowserIconButton icon="minimize" label="Minimize viewer" onClick={onMinimize} />
        </header>
        <div
          class="browser-viewer-body"
          id={`${id}-page`}
          role={showingBitwarden ? "region" : showTabs ? "tabpanel" : undefined}
          aria-label={showingBitwarden ? "Bitwarden" : undefined}
          aria-labelledby={
            showTabs && !showingBitwarden && selected ? `${id}-${selected.id}` : undefined
          }
        >
          {children}
        </div>
        {mode === "floating" &&
          edges.map((edge) => (
            <div
              key={edge}
              class="browser-resize-handle"
              data-edge={edge}
              onPointerDown={(event) => onResizePointerDown(event, edge)}
            />
          ))}
        {docked && (
          <div
            class="browser-resize-handle browser-divider"
            data-edge={dividerEdge[mode]}
            role="separator"
            aria-label="Browser split"
            aria-orientation={mode === "left" || mode === "right" ? "vertical" : "horizontal"}
            aria-valuenow={Math.round(split)}
            tabindex={0}
            onPointerDown={(event) => onResizePointerDown(event, dividerEdge[mode])}
            onKeyDown={onDividerKeyDown}
          />
        )}
      </section>
      {preview && <div aria-hidden="true" class="browser-snap-preview" data-edge={preview} />}
    </>
  );
}
