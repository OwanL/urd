import { useEffect, useState } from "preact/hooks";
import { Tooltip } from "../../controls/Tooltip.tsx";
import { UsageControl } from "../../usage/UsageControl.tsx";
import type { SessionContextUsage, SessionWorkingTime } from "../../sessions/SessionSnapshot.ts";
import { formatTokenCount } from "../TokenCountFormatting.ts";
import { PromptCacheDetails } from "./PromptCacheDetails.tsx";
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
  compaction?: {
    canCompact: boolean;
    status: "requesting" | "queued" | "running" | null;
    message: string | null;
    unavailableReason: string | null;
    onCheck: (() => void) | undefined;
    onCompact(): void;
    onCancel: (() => void) | undefined;
  };
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
  compaction,
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
  const {
    status: compactionStatus = null,
    message: compactionMessage,
    unavailableReason: compactionUnavailableReason,
    canCompact = false,
    onCheck: onCheckCompaction,
    onCancel: onCancelCompaction,
  } = compaction ?? {};
  const cancelAvailable = compactionStatus === "queued" && onCancelCompaction !== undefined;
  const compactDisabled = compactionStatus !== null || !canCompact;
  const compactStatus =
    compactionStatus === "running"
      ? "Compacting…"
      : compactionStatus === "queued"
        ? "Queued"
        : compactionStatus === "requesting"
          ? "Requesting…"
          : null;
  const compactAction = compactStatus ?? (onCheckCompaction ? "Check compaction" : "Compact");
  const actionAvailable =
    compactionStatus === null && (canCompact || onCheckCompaction !== undefined);
  const compactionHelp =
    !compactStatus && !actionAvailable && compactionUnavailableReason !== compactionMessage
      ? compactionUnavailableReason
      : null;
  const contextAccessibleLabel = `Context window: ${contextLabel}${contextDetail ? `, ${contextDetail.toLowerCase()}` : ""}`;
  const compactionAccessibleLabel = !compaction
    ? ""
    : cancelAvailable
      ? ". Compaction queued. Cancel compaction"
      : compactStatus === "Queued"
        ? ". Compaction queued"
        : `. ${actionAvailable || compactStatus ? compactAction : "Compaction unavailable"}`;
  const ratio = contextUsage && contextWindow ? contextUsage.usedTokens / contextWindow : 0;
  const incomplete = workingTime?.hasUnknownDuration || (fenced && Boolean(startedAt));
  const duration =
    workingTime && (startedAt || workingTime.completedMilliseconds > 0 || incomplete)
      ? formatWorkingTime(
          workingMilliseconds(fenced ? { ...workingTime, startedAt: null } : workingTime, now),
        )
      : null;

  return (
    <div class="session-indicators">
      {duration !== null ? (
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
      ) : null}
      <div class="session-indicator-item">
        <span class="session-indicator-name" aria-hidden="true">
          Session cost
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
          disabled={
            compactionStatus === "queued" ||
            (!usageActive && !compactStatus && !compactionMessage && !compactionHelp)
          }
          content={
            <div class="session-context-details">
              {usageActive && (
                <PromptCacheDetails
                  key={sessionId}
                  sessionId={sessionId}
                  refreshKey={usageRefresh}
                />
              )}
              {compactStatus && <span class="tooltip-detail">{compactStatus}</span>}
              {compactionMessage && <span class="tooltip-detail">{compactionMessage}</span>}
              {compactionHelp && <span class="tooltip-detail">{compactionHelp}</span>}
            </div>
          }
        >
          <button
            type="button"
            class={`session-indicator session-context-indicator ${compactionStatus ? "has-compaction-status" : actionAvailable ? "has-compaction-action" : ""} ${cancelAvailable ? "can-cancel-compaction" : ""} ${ratio >= 0.85 ? "is-critical" : ratio >= 0.7 ? "is-warning" : ""}`}
            aria-label={`${contextAccessibleLabel}${compactionAccessibleLabel}`}
            aria-disabled={
              compaction ? !cancelAvailable && compactDisabled && !actionAvailable : undefined
            }
            onClick={() => {
              if (cancelAvailable) onCancelCompaction();
              else if (compactionStatus !== null) return;
              else if (onCheckCompaction) onCheckCompaction();
              else if (!compactDisabled) compaction?.onCompact();
            }}
          >
            <span class="session-context-value" aria-hidden="true">
              {contextLabel}
            </span>
            {compaction && (
              <span class="session-context-action" aria-hidden="true">
                {compactAction}
              </span>
            )}
            {cancelAvailable && (
              <span class="session-context-cancel" aria-hidden="true">
                Cancel
              </span>
            )}
          </button>
        </Tooltip>
      </div>
    </div>
  );
}
