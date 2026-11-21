import { useEffect, useState } from "preact/hooks";
import { Tooltip } from "../../controls/Tooltip.tsx";
import { UsageControl } from "../../usage/UsageControl.tsx";
import type { SessionContextUsage, SessionWorkingTime } from "../../sessions/SessionSnapshot.ts";
import { formatTokenCount } from "../TokenCountFormatting.ts";
import { formatWorkingTime, workingMilliseconds } from "./SessionWorkingTime.ts";

interface SessionIndicatorsProps {
  contextUsage: SessionContextUsage | null;
  contextWindow: number | null;
  workingTime: SessionWorkingTime | null;
  active: boolean;
  fenced: boolean;
  sessionId: string;
  usageActive: boolean;
  usageBusy: boolean;
  usageRefresh: number | string;
  canCompact: boolean;
  compactionStatus: "requesting" | "queued" | "running" | null;
  compactionMessage: string | null;
  compactionUnavailableReason: string | null;
  onCheckCompaction: (() => void) | undefined;
  onCompact(): void;
}

export function SessionIndicators({
  contextUsage,
  contextWindow,
  workingTime,
  active,
  fenced,
  sessionId,
  usageActive,
  usageBusy,
  usageRefresh,
  canCompact,
  compactionStatus,
  compactionMessage,
  compactionUnavailableReason,
  onCheckCompaction,
  onCompact,
}: SessionIndicatorsProps) {
  const [now, setNow] = useState(Date.now);
  const startedAt = workingTime?.startedAt;
  useEffect(() => {
    if (!active || !startedAt || fenced) return;
    setNow(Date.now());
    const timer = setInterval(() => setNow(Date.now()), 1_000);
    return () => clearInterval(timer);
  }, [active, startedAt, fenced]);

  const source = contextUsage?.source;
  const contextLabel = `${source === "estimated" ? "~" : ""}${formatTokenCount(contextUsage?.usedTokens)}/${formatTokenCount(contextWindow)}`;
  const contextDetail =
    source === "estimated"
      ? "Estimated usage"
      : source === "lastMeasured"
        ? "Last measured usage"
        : null;
  const compactDisabled = !canCompact;
  const compactStatus =
    compactionStatus === "running"
      ? "Compacting…"
      : compactionStatus === "queued"
        ? "Queued"
        : compactionStatus === "requesting"
          ? "Requesting…"
          : null;
  const compactAction = compactStatus ?? (onCheckCompaction ? "Check compaction" : "Compact");
  const actionAvailable = canCompact || onCheckCompaction !== undefined;
  const contextAccessibleLabel = `Context window: ${contextLabel}${contextDetail ? `, ${contextDetail.toLowerCase()}` : ""}`;
  const ratio = contextUsage && contextWindow ? contextUsage.usedTokens / contextWindow : 0;
  const incomplete = workingTime?.hasUnknownDuration || (fenced && Boolean(startedAt));
  const duration = workingTime
    ? formatWorkingTime(
        workingMilliseconds(fenced ? { ...workingTime, startedAt: null } : workingTime, now),
      )
    : null;

  return (
    <div class="session-indicators">
      <div class="session-indicator-item">
        <span class="session-indicator-name" aria-hidden="true">
          Cost
        </span>
        <UsageControl
          label="Session"
          sessionId={sessionId}
          active={usageActive}
          busy={usageBusy}
          refreshKey={usageRefresh}
          class="session-usage-control"
        />
      </div>
      <div class="session-indicator-item">
        <span class="session-indicator-name" aria-hidden="true">
          Context
        </span>
        <Tooltip
          dismissOnClick={false}
          content={
            <div class="session-context-details">
              <span>Context window</span>
              <span class="tooltip-detail">{contextLabel}</span>
              {contextDetail && <span class="tooltip-detail">{contextDetail}</span>}
              {compactStatus && <span class="tooltip-detail">{compactStatus}</span>}
              {compactionMessage && <span class="tooltip-detail">{compactionMessage}</span>}
              {!compactStatus &&
                !actionAvailable &&
                compactionUnavailableReason &&
                compactionUnavailableReason !== compactionMessage && (
                  <span class="tooltip-detail">{compactionUnavailableReason}</span>
                )}
            </div>
          }
        >
          <button
            type="button"
            class={`session-indicator session-context-indicator ${compactionStatus ? "has-compaction-status" : actionAvailable ? "has-compaction-action" : ""} ${ratio >= 0.85 ? "is-critical" : ratio >= 0.7 ? "is-warning" : ""}`}
            aria-label={`${contextAccessibleLabel}. ${compactStatus === "Queued" ? "Compaction queued" : actionAvailable || compactStatus ? compactAction : "Compaction unavailable"}`}
            aria-disabled={compactDisabled && !onCheckCompaction}
            onClick={() => {
              if (onCheckCompaction) onCheckCompaction();
              else if (!compactDisabled) onCompact();
            }}
          >
            <span class="session-context-value" aria-hidden="true">
              {contextLabel}
            </span>
            <span class="session-context-action" aria-hidden="true">
              {compactAction}
            </span>
          </button>
        </Tooltip>
      </div>
      {duration !== null && (
        <div class="session-indicator-item">
          <span class="session-indicator-name" aria-hidden="true">
            Time
          </span>
          <Tooltip
            content={
              <>
                Agent working time
                <span class="tooltip-detail">Idle time excluded.</span>
                {incomplete && (
                  <span class="tooltip-detail">Interrupted work has unknown duration.</span>
                )}
              </>
            }
          >
            <button
              type="button"
              class="session-indicator"
              aria-label={`Agent working time: ${duration}${incomplete ? ", incomplete" : ""}`}
            >
              {incomplete ? "≥" : ""}
              {duration}
            </button>
          </Tooltip>
        </div>
      )}
    </div>
  );
}
