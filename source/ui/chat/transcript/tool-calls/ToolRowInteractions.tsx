import { createContext, type ComponentChildren } from "preact";
import { useCallback, useContext, useEffect, useMemo, useRef, useState } from "preact/hooks";
import { Popover } from "../../../controls/Popover.tsx";
import type { SessionClient } from "../../../sessions/SessionClient.ts";
import { reusableToolCallPreview, toolCallClipboardText } from "./ToolCallInspection.ts";
import type {
  ActiveTarget,
  CopyState,
  InspectionTarget,
  MenuTarget,
  Preview,
} from "./ToolRowInteractionTypes.ts";
import { ToolFileMenu } from "./ToolFileMenu.tsx";
import { ToolInspection } from "./ToolInspection.tsx";
import { useToolPreview } from "./useToolPreview.ts";
import { useToolAnchorVisibility } from "./useToolAnchorVisibility.ts";
import { proposedMcpUrl, type McpConnectionReviewDetail } from "./McpConnectionReview.ts";
import "./tool-row-interactions.css";

interface ToolRowInteractionContext {
  readonly openInspection: (
    anchor: HTMLElement,
    requestId: string,
    callId: string,
    returned: boolean,
    label: string,
    payloadFinished: boolean,
    pin: boolean,
  ) => void;
  readonly scheduleInspection: (
    anchor: HTMLElement,
    requestId: string,
    callId: string,
    returned: boolean,
    label: string,
    payloadFinished: boolean,
  ) => void;
  readonly reviewMcpConnection: (
    anchor: HTMLElement,
    requestId: string,
    callId: string,
    signal: AbortSignal,
  ) => Promise<void>;
  readonly leaveSource: (anchor: HTMLElement) => void;
  readonly openFileMenu: (anchor: HTMLElement, path: string) => void;
  readonly isOpen: (anchor: HTMLElement, kind: ActiveTarget["kind"]) => boolean;
  readonly sourceUnmounted: (anchor: HTMLElement) => void;
  readonly sourceChanged: (anchor: HTMLElement, payloadFinished: boolean) => void;
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
      payloadFinished: boolean,
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
        payloadFinished,
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
    (
      anchor: HTMLElement,
      requestId: string,
      callId: string,
      returned: boolean,
      label: string,
      payloadFinished: boolean,
    ) => {
      clearTimeout(hoverTimer.current);
      clearTimeout(closeTimer.current);
      closeTimer.current = undefined;
      hoverTimer.current = window.setTimeout(() => {
        hoverTimer.current = undefined;
        openInspection(anchor, requestId, callId, returned, label, payloadFinished, false);
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
  const sourceChanged = useCallback((anchor: HTMLElement, payloadFinished: boolean) => {
    const current = activeRef.current;
    if (current?.kind !== "inspection" || current.anchor !== anchor) return;
    if (current.payloadFinished !== payloadFinished) {
      const updated = { ...current, payloadFinished };
      activeRef.current = updated;
      setActive(updated);
    }
    setPreviewVersion((version) => version + 1);
  }, []);

  const isOpen = useCallback(
    (anchor: HTMLElement, kind: ActiveTarget["kind"]) =>
      active?.anchor === anchor && active.kind === kind,
    [active],
  );

  const reviewMcpConnection = useCallback(
    async (anchor: HTMLElement, requestId: string, callId: string, signal: AbortSignal) => {
      const revision = revisionRef.current;
      if (revision === null) throw new Error("Transcript is unavailable.");
      const transcript = await client.readToolCallTranscript(
        sessionId,
        requestId,
        callId,
        { expectedHistoryRevision: revision, returned: false, full: true },
        signal,
      );
      if (signal.aborted || !anchor.isConnected) return;
      if (revisionRef.current !== revision) throw new Error("Transcript changed.");
      const url =
        transcript && !transcript.truncated && transcript.projectionFinal
          ? proposedMcpUrl(transcript.input)
          : null;
      if (!url) throw new Error("Invalid connection proposal.");
      anchor.dispatchEvent(
        new CustomEvent<McpConnectionReviewDetail>("urd:review-mcp-connection", {
          bubbles: true,
          detail: { url },
        }),
      );
    },
    [client, sessionId],
  );

  const interactions = useMemo(
    () => ({
      openInspection,
      reviewMcpConnection,
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
      reviewMcpConnection,
      scheduleInspection,
      sourceUnmounted,
      sourceChanged,
    ],
  );

  const activeHistoryRevision = active?.kind === "inspection" ? active.historyRevision : null;
  useEffect(() => {
    if (activeHistoryRevision !== null && activeHistoryRevision !== historyRevision) dismiss(false);
  }, [activeHistoryRevision, dismiss, historyRevision]);

  useToolPreview(active, activeRef, client, sessionId, previewVersion, setPreview);

  useToolAnchorVisibility(active, activeRef, root, dismiss);

  async function copyFullTranscript(target: InspectionTarget) {
    if (copyController.current) return;
    const controller = new AbortController();
    copyController.current = controller;
    clearTimeout(copyTimer.current);
    setCopyState("copying");
    try {
      const current = activeRef.current;
      const readyPreview =
        current?.kind === "inspection" && current.id === target.id
          ? reusableToolCallPreview(
              preview?.state === "ready" ? preview : null,
              current,
              revisionRef.current,
            )
          : null;
      const transcript =
        readyPreview ??
        (await client.readToolCallTranscript(
          sessionId,
          target.requestId,
          target.callId,
          {
            expectedHistoryRevision: target.historyRevision,
            returned: target.returned,
            full: true,
          },
          controller.signal,
        ));
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
            <ToolInspection
              active={active}
              shownPreview={shownPreview}
              copyState={copyState}
              onPointerEnter={() => {
                clearTimeout(closeTimer.current);
                closeTimer.current = undefined;
              }}
              onPointerLeave={() => leaveSource(active.anchor)}
              onCopy={() => void copyFullTranscript(active)}
            />
          ) : (
            <ToolFileMenu
              firstMenuItem={firstMenuItem}
              secondMenuItem={secondMenuItem}
              shownMenuMessage={shownMenuMessage}
              onCopy={() => void copyPath(active)}
              onReveal={() => void revealPath(active)}
            />
          )}
        </Popover>
      )}
      <span class="tool-overlay-announcement" role="status" aria-live="polite">
        {announcement}
      </span>
    </ToolRowInteractionContext.Provider>
  );
}
