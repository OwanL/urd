import { createContext, type ComponentChildren } from "preact";
import { useCallback, useContext, useEffect, useMemo, useRef, useState } from "preact/hooks";
import { Popover } from "../components/Popover.tsx";
import type { SessionClient } from "../sessions/SessionClient.ts";
import type { ToolCallTranscript } from "../sessions/Messages.ts";
import { formatToolInput, toolCallClipboardText } from "./ToolCallInspection.ts";
import "./tool-row-interactions.css";

interface InspectionTarget {
  readonly kind: "inspection";
  readonly id: number;
  readonly anchor: HTMLElement;
  readonly requestId: string;
  readonly callId: string;
  readonly returned: boolean;
  readonly historyRevision: number;
  readonly label: string;
  readonly pinned: boolean;
}

interface MenuTarget {
  readonly kind: "menu";
  readonly id: number;
  readonly anchor: HTMLElement;
  readonly path: string;
}

type ActiveTarget = InspectionTarget | MenuTarget;
type Preview =
  | { readonly id: number; readonly state: "loading" }
  | { readonly id: number; readonly state: "ready"; readonly transcript: ToolCallTranscript }
  | { readonly id: number; readonly state: "missing" }
  | { readonly id: number; readonly state: "error" };
type CopyState = "idle" | "copying" | "copied" | "error";

interface ToolRowInteractionContext {
  readonly openInspection: (
    anchor: HTMLElement,
    requestId: string,
    callId: string,
    returned: boolean,
    label: string,
    pin: boolean,
  ) => void;
  readonly scheduleInspection: (
    anchor: HTMLElement,
    requestId: string,
    callId: string,
    returned: boolean,
    label: string,
  ) => void;
  readonly leaveSource: (anchor: HTMLElement) => void;
  readonly openFileMenu: (anchor: HTMLElement, path: string) => void;
  readonly isOpen: (anchor: HTMLElement, kind: ActiveTarget["kind"]) => boolean;
  readonly sourceUnmounted: (anchor: HTMLElement) => void;
  readonly sourceChanged: (anchor: HTMLElement) => void;
}

const ToolRowInteractionContext = createContext<ToolRowInteractionContext | null>(null);

export function useToolRowInteractions(): ToolRowInteractionContext {
  const interactions = useContext(ToolRowInteractionContext);
  if (!interactions) throw new Error("Tool rows must be rendered inside a transcript.");
  return interactions;
}

export function ToolRowInteractionsProvider({
  children,
  client,
  sessionId,
  historyRevision,
  root,
}: {
  children: ComponentChildren;
  client: SessionClient;
  sessionId: string;
  historyRevision: number | null;
  root: HTMLElement | null;
}) {
  const [active, setActive] = useState<ActiveTarget | null>(null);
  const activeRef = useRef<ActiveTarget | null>(null);
  const revisionRef = useRef(historyRevision);
  revisionRef.current = historyRevision;
  const [preview, setPreview] = useState<Preview | null>(null);
  const [previewVersion, setPreviewVersion] = useState(0);
  const [copyState, setCopyState] = useState<CopyState>("idle");
  const [menuMessage, setMenuMessage] = useState<{ id: number; text: string } | null>(null);
  const [announcement, setAnnouncement] = useState("");
  const nextId = useRef(0);
  const hoverTimer = useRef<number | undefined>(undefined);
  const closeTimer = useRef<number | undefined>(undefined);
  const copyTimer = useRef<number | undefined>(undefined);
  const menuMessageTimer = useRef<number | undefined>(undefined);
  const announcementTimer = useRef<number | undefined>(undefined);
  const copyController = useRef<AbortController | null>(null);
  const popoverAnchor = useRef<HTMLElement | null>(null);
  const firstMenuItem = useRef<HTMLButtonElement>(null);
  const secondMenuItem = useRef<HTMLButtonElement>(null);
  popoverAnchor.current = active?.anchor ?? null;

  const dismiss = useCallback((returnFocus: boolean) => {
    clearTimeout(hoverTimer.current);
    clearTimeout(closeTimer.current);
    hoverTimer.current = undefined;
    closeTimer.current = undefined;
    copyController.current?.abort();
    copyController.current = null;
    const current = activeRef.current;
    if (!current) return;
    activeRef.current = null;
    setActive(null);
    setPreview(null);
    setCopyState("idle");
    setMenuMessage(null);
    if (returnFocus && current.anchor.isConnected) current.anchor.focus({ preventScroll: true });
  }, []);

  const openInspection = useCallback(
    (
      anchor: HTMLElement,
      requestId: string,
      callId: string,
      returned: boolean,
      label: string,
      pin: boolean,
    ) => {
      const current = activeRef.current;
      if (!pin && current?.kind === "inspection" && current.pinned) return;
      clearTimeout(hoverTimer.current);
      clearTimeout(closeTimer.current);
      hoverTimer.current = undefined;
      closeTimer.current = undefined;
      if (current?.kind === "inspection" && current.anchor === anchor) {
        if (!pin) return;
        if (current.pinned) {
          dismiss(false);
          return;
        }
        const pinned = { ...current, pinned: true };
        activeRef.current = pinned;
        setActive(pinned);
        return;
      }
      const revision = revisionRef.current;
      if (revision === null) return;
      const next: InspectionTarget = {
        kind: "inspection",
        id: ++nextId.current,
        anchor,
        requestId,
        callId,
        returned,
        historyRevision: revision,
        label,
        pinned: pin,
      };
      copyController.current?.abort();
      copyController.current = null;
      activeRef.current = next;
      setActive(next);
      setPreview(null);
      setCopyState("idle");
      setMenuMessage(null);
    },
    [dismiss],
  );

  const scheduleInspection = useCallback(
    (anchor: HTMLElement, requestId: string, callId: string, returned: boolean, label: string) => {
      clearTimeout(hoverTimer.current);
      clearTimeout(closeTimer.current);
      closeTimer.current = undefined;
      hoverTimer.current = window.setTimeout(() => {
        hoverTimer.current = undefined;
        openInspection(anchor, requestId, callId, returned, label, false);
      }, 300);
    },
    [openInspection],
  );

  const leaveSource = useCallback(
    (anchor: HTMLElement) => {
      clearTimeout(hoverTimer.current);
      hoverTimer.current = undefined;
      clearTimeout(closeTimer.current);
      closeTimer.current = window.setTimeout(() => {
        closeTimer.current = undefined;
        const current = activeRef.current;
        if (current?.anchor === anchor && (current.kind !== "inspection" || !current.pinned))
          dismiss(false);
      }, 220);
    },
    [dismiss],
  );

  const openFileMenu = useCallback((anchor: HTMLElement, path: string) => {
    clearTimeout(hoverTimer.current);
    clearTimeout(closeTimer.current);
    hoverTimer.current = undefined;
    closeTimer.current = undefined;
    copyController.current?.abort();
    copyController.current = null;
    const next: MenuTarget = {
      kind: "menu",
      id: ++nextId.current,
      anchor,
      path,
    };
    activeRef.current = next;
    setActive(next);
    setPreview(null);
    setCopyState("idle");
    setMenuMessage(null);
  }, []);

  const sourceUnmounted = useCallback(
    (anchor: HTMLElement) => {
      if (activeRef.current?.anchor === anchor) dismiss(false);
    },
    [dismiss],
  );

  // A call that finishes while inspected reloads its preview in place.
  const sourceChanged = useCallback((anchor: HTMLElement) => {
    const current = activeRef.current;
    if (current?.kind === "inspection" && current.anchor === anchor)
      setPreviewVersion((version) => version + 1);
  }, []);

  const isOpen = useCallback(
    (anchor: HTMLElement, kind: ActiveTarget["kind"]) =>
      active?.anchor === anchor && active.kind === kind,
    [active],
  );

  const interactions = useMemo(
    () => ({
      openInspection,
      scheduleInspection,
      leaveSource,
      openFileMenu,
      isOpen,
      sourceUnmounted,
      sourceChanged,
    }),
    [
      isOpen,
      leaveSource,
      openFileMenu,
      openInspection,
      scheduleInspection,
      sourceUnmounted,
      sourceChanged,
    ],
  );

  const activeHistoryRevision = active?.kind === "inspection" ? active.historyRevision : null;
  useEffect(() => {
    if (activeHistoryRevision !== null && activeHistoryRevision !== historyRevision) dismiss(false);
  }, [activeHistoryRevision, dismiss, historyRevision]);

  useEffect(() => {
    const target = activeRef.current;
    if (!target || target.kind !== "inspection") return;
    const controller = new AbortController();
    setPreview((current) =>
      current?.id === target.id && current.state === "ready"
        ? current
        : { id: target.id, state: "loading" },
    );
    void client
      .readToolCallTranscript(
        sessionId,
        target.requestId,
        target.callId,
        {
          expectedHistoryRevision: target.historyRevision,
          returned: target.returned,
          full: false,
        },
        controller.signal,
      )
      .then((transcript) => {
        if (controller.signal.aborted || activeRef.current?.id !== target.id) return;
        setPreview(
          transcript
            ? { id: target.id, state: "ready", transcript }
            : { id: target.id, state: "missing" },
        );
      })
      .catch(() => {
        if (!controller.signal.aborted && activeRef.current?.id === target.id)
          setPreview({ id: target.id, state: "error" });
      });
    return () => controller.abort();
  }, [active?.id, client, previewVersion, sessionId]);

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
  }, [activeId, dismiss, root]);

  async function copyFullTranscript(target: InspectionTarget) {
    const controller = new AbortController();
    copyController.current?.abort();
    copyController.current = controller;
    setCopyState("copying");
    try {
      const transcript = await client.readToolCallTranscript(
        sessionId,
        target.requestId,
        target.callId,
        {
          expectedHistoryRevision: target.historyRevision,
          returned: target.returned,
          full: true,
        },
        controller.signal,
      );
      if (controller.signal.aborted || activeRef.current?.id !== target.id) return;
      if (!transcript) throw new Error("Tool call is no longer available.");
      if (!navigator.clipboard?.writeText) throw new Error("Clipboard is unavailable.");
      await navigator.clipboard.writeText(toolCallClipboardText(transcript));
      if (controller.signal.aborted || activeRef.current?.id !== target.id) return;
      setCopyState("copied");
      copyTimer.current = window.setTimeout(() => {
        if (activeRef.current?.id === target.id) setCopyState("idle");
      }, 1_500);
    } catch {
      if (!controller.signal.aborted && activeRef.current?.id === target.id) setCopyState("error");
    } finally {
      if (copyController.current === controller) copyController.current = null;
    }
  }

  function showMenuMessage(target: MenuTarget, text: string) {
    if (activeRef.current?.id !== target.id) return;
    clearTimeout(menuMessageTimer.current);
    setMenuMessage({ id: target.id, text });
    menuMessageTimer.current = window.setTimeout(() => {
      if (activeRef.current?.id === target.id) setMenuMessage(null);
      menuMessageTimer.current = undefined;
    }, 2_200);
  }

  async function copyPath(target: MenuTarget) {
    try {
      if (!navigator.clipboard?.writeText) throw new Error("Clipboard is unavailable.");
      await navigator.clipboard.writeText(target.path);
      if (activeRef.current?.id !== target.id) return;
      setAnnouncement("Path copied.");
      clearTimeout(announcementTimer.current);
      announcementTimer.current = window.setTimeout(() => setAnnouncement(""), 2_000);
      dismiss(true);
    } catch {
      showMenuMessage(target, "Could not copy path.");
    }
  }

  async function revealPath(target: MenuTarget) {
    try {
      await client.revealPath(target.path);
      if (activeRef.current?.id === target.id) dismiss(true);
    } catch {
      showMenuMessage(target, "Could not reveal in Explorer.");
    }
  }

  function moveMenuFocus(event: KeyboardEvent) {
    const items = [firstMenuItem.current, secondMenuItem.current].filter(
      (item): item is HTMLButtonElement => item !== null,
    );
    if (items.length === 0) return;
    const currentIndex = items.indexOf(document.activeElement as HTMLButtonElement);
    if (event.key === "ArrowDown" || event.key === "ArrowUp") {
      event.preventDefault();
      const offset = event.key === "ArrowDown" ? 1 : -1;
      items[(currentIndex + offset + items.length) % items.length]?.focus();
    } else if (event.key === "Home" || event.key === "End") {
      event.preventDefault();
      items[event.key === "Home" ? 0 : items.length - 1]?.focus();
    }
  }

  const shownPreview = active?.kind === "inspection" && preview?.id === active.id ? preview : null;
  const shownMenuMessage =
    active?.kind === "menu" && menuMessage?.id === active.id ? menuMessage.text : null;

  return (
    <ToolRowInteractionContext.Provider value={interactions}>
      {children}
      {active && (
        <Popover
          key={active.id}
          anchor={popoverAnchor}
          class={active.kind === "inspection" ? "tool-inspection-popover" : "tool-file-menu"}
          label={active.kind === "inspection" ? "Tool call details" : "File actions"}
          role={active.kind === "inspection" ? "dialog" : "menu"}
          placement="below"
          {...(active.kind === "menu" ? { initialFocus: firstMenuItem } : {})}
          autoFocus={active.kind === "menu" || active.pinned}
          onClose={(returnFocus) => {
            if (activeRef.current?.id === active.id) dismiss(returnFocus);
          }}
        >
          {active.kind === "inspection" ? (
            <div
              class="tool-inspection"
              onPointerEnter={() => {
                clearTimeout(closeTimer.current);
                closeTimer.current = undefined;
              }}
              onPointerLeave={() => leaveSource(active.anchor)}
            >
              <div class="tool-inspection-header">
                <span>{active.label}</span>
                <button
                  type="button"
                  class="tool-overlay-action"
                  aria-label="Copy full tool input and output"
                  disabled={copyState === "copying"}
                  onClick={() => void copyFullTranscript(active)}
                >
                  {copyState === "copying"
                    ? "Copying…"
                    : copyState === "copied"
                      ? "Copied"
                      : copyState === "error"
                        ? "Copy failed"
                        : "Copy"}
                </button>
              </div>
              {shownPreview?.state === "missing" && (
                <p class="tool-inspection-message">Tool call is no longer available.</p>
              )}
              {shownPreview?.state === "error" && (
                <p class="tool-inspection-message" role="status">
                  Could not load tool call.
                </p>
              )}
              <div class="tool-inspection-section">
                <h3>Input</h3>
                <pre>{previewText(shownPreview, "input")}</pre>
              </div>
              <div class="tool-inspection-section">
                <h3>Output</h3>
                <pre>{previewText(shownPreview, "output")}</pre>
              </div>
              {shownPreview?.state === "ready" && shownPreview.transcript.truncated && (
                <p class="tool-inspection-note">
                  Preview truncated. Copy fetches the full payload.
                </p>
              )}
              <span class="tool-inspection-feedback" aria-live="polite">
                {copyState === "error" ? "Could not copy tool call." : ""}
              </span>
            </div>
          ) : (
            <div class="tool-file-menu-items" onKeyDown={moveMenuFocus}>
              <button
                ref={firstMenuItem}
                type="button"
                role="menuitem"
                class="tool-file-menu-item"
                onClick={() => void copyPath(active)}
              >
                Copy path
              </button>
              <button
                ref={secondMenuItem}
                type="button"
                role="menuitem"
                class="tool-file-menu-item"
                onClick={() => void revealPath(active)}
              >
                Reveal in Explorer
              </button>
              {shownMenuMessage && (
                <p class="tool-file-menu-error" role="status">
                  {shownMenuMessage}
                </p>
              )}
            </div>
          )}
        </Popover>
      )}
      <span class="tool-overlay-announcement" role="status" aria-live="polite">
        {announcement}
      </span>
    </ToolRowInteractionContext.Provider>
  );
}

function previewText(preview: Preview | null, part: "input" | "output"): string {
  if (!preview || preview.state === "loading") return "Loading…";
  if (preview.state !== "ready") return "—";
  if (part === "input") return formatToolInput(preview.transcript.input);
  return preview.transcript.output ?? "Running…";
}
