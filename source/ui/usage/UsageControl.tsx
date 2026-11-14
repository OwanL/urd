import { useEffect, useId, useMemo, useRef, useState } from "preact/hooks";
import { Popover } from "../components/Popover.tsx";
import type { UsageRange } from "./UsageTypes.ts";
import { UsageClient } from "./UsageClient.ts";
import { UsageDetails } from "./UsageDetails.tsx";
import "./usage.css";
import { formatUsageCost } from "./UsageValues.ts";
import { useUsageSummary } from "./useUsageSummary.ts";

export function UsageControl({
  label,
  sessionId,
  range,
  active = true,
  busy = false,
  refreshKey = 0,
  class: className,
}: {
  label: string;
  sessionId?: string;
  range?: UsageRange;
  active?: boolean;
  busy?: boolean;
  refreshKey?: number | string;
  class?: string;
}) {
  const client = useMemo(() => new UsageClient(new URL(window.location.href)), []);
  const anchor = useRef<HTMLButtonElement>(null);
  const popoverAnchor = useRef<HTMLElement | null>(null);
  const popoverId = useId();
  const [open, setOpen] = useState(false);
  const [pinned, setPinned] = useState(false);
  const enterTimer = useRef<number | undefined>(undefined);
  const closeTimer = useRef<number | undefined>(undefined);
  popoverAnchor.current = open ? anchor.current : null;
  const summaryState = useUsageSummary({
    client,
    enabled: active,
    pollMilliseconds: busy ? 1_000 : 15_000,
    refreshKey,
    ...(sessionId ? { sessionId } : { range: range ?? "today" }),
    detail: false,
  });

  useEffect(() => {
    if (active) return;
    clearTimeout(enterTimer.current);
    clearTimeout(closeTimer.current);
    setOpen(false);
    setPinned(false);
  }, [active]);
  useEffect(
    () => () => {
      clearTimeout(enterTimer.current);
      clearTimeout(closeTimer.current);
    },
    [],
  );

  const value = summaryState.summary
    ? formatUsageCost(summaryState.summary.totals)
    : summaryState.stale
      ? "Unavailable"
      : "…";
  const staleLabel = summaryState.stale ? (summaryState.summary ? ", stale" : ", unavailable") : "";
  const accessibleName = sessionId
    ? `Session API-equivalent cost: ${value}${staleLabel}`
    : `${label} API-equivalent cost: ${value}${staleLabel}`;

  function closeSoon() {
    clearTimeout(closeTimer.current);
    if (!pinned)
      closeTimer.current = window.setTimeout(() => {
        const focused = document.activeElement;
        if (focused === anchor.current || document.getElementById(popoverId)?.contains(focused))
          return;
        setOpen(false);
      }, 220);
  }

  return (
    <>
      <button
        ref={anchor}
        type="button"
        class={`usage-control${className ? ` ${className}` : ""}`}
        aria-label={accessibleName}
        aria-haspopup="dialog"
        aria-controls={popoverId}
        aria-expanded={open}
        onPointerEnter={(event) => {
          if (!active || pinned || event.pointerType === "touch") return;
          clearTimeout(closeTimer.current);
          enterTimer.current = window.setTimeout(() => {
            setOpen(true);
            setPinned(false);
          }, 300);
        }}
        onPointerLeave={() => {
          clearTimeout(enterTimer.current);
          closeSoon();
        }}
        onFocus={() => {
          clearTimeout(enterTimer.current);
          clearTimeout(closeTimer.current);
          setPinned(false);
          setOpen(true);
        }}
        onClick={() => {
          clearTimeout(enterTimer.current);
          clearTimeout(closeTimer.current);
          if (open && pinned) {
            setOpen(false);
            setPinned(false);
            return;
          }
          setPinned(true);
          setOpen(true);
        }}
      >
        {!sessionId && <span class="usage-control-label">{label}</span>}
        <span class="usage-control-value">{value}</span>
        {summaryState.stale && (
          <span class="usage-stale-mark" aria-hidden="true">
            · stale
          </span>
        )}
      </button>
      {open && active && (
        <Popover
          anchor={popoverAnchor}
          id={popoverId}
          class="usage-popover"
          label={`Usage details: ${sessionId ? "this session" : label}`}
          placement="below"
          autoFocus={pinned}
          onClose={() => {
            clearTimeout(closeTimer.current);
            setOpen(false);
            setPinned(false);
          }}
        >
          <div
            onPointerEnter={() => {
              clearTimeout(closeTimer.current);
            }}
            onPointerLeave={closeSoon}
          >
            <UsageDetails
              client={client}
              label={label}
              {...(sessionId ? { sessionId } : { range: range ?? "today" })}
              active={active}
              busy={busy}
              refreshKey={refreshKey}
            />
          </div>
        </Popover>
      )}
    </>
  );
}
