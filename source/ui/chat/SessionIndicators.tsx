import { useEffect, useState } from "preact/hooks";
import { Tooltip } from "../components/Tooltip.tsx";
import { UsageControl } from "../usage/UsageControl.tsx";
import type { SessionContextUsage, SessionWorkingTime } from "../sessions/SessionSnapshot.ts";
import {
  formatContextTokens,
  formatWorkingTime,
  workingMilliseconds,
} from "./SessionIndicatorValues.ts";

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
}: SessionIndicatorsProps) {
  const [now, setNow] = useState(Date.now);
  const startedAt = workingTime?.startedAt;
  useEffect(() => {
    if (!active || !startedAt || fenced) return;
    setNow(Date.now());
    const timer = setInterval(() => setNow(Date.now()), 1_000);
    return () => clearInterval(timer);
  }, [active, startedAt, fenced]);

  const contextLabel = `${formatContextTokens(contextUsage?.usedTokens)}/${formatContextTokens(contextWindow)}`;
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
          content={
            <>
              Context window
              <span class="tooltip-detail">
                {contextUsage
                  ? `${contextUsage.isEstimate ? "Estimated" : "Last reported"}: ${contextUsage.usedTokens.toLocaleString()} tokens`
                  : "Usage unknown"}
                {contextWindow ? ` / ${contextWindow.toLocaleString()}` : " / limit unknown"}
              </span>
              <span class="tooltip-detail">Pending input and in-flight output not included.</span>
            </>
          }
        >
          <button
            type="button"
            class={`session-indicator ${ratio >= 0.85 ? "is-critical" : ratio >= 0.7 ? "is-warning" : ""}`}
            aria-label={`Context window: ${contextLabel}${contextUsage?.isEstimate ? ", estimated" : ""}`}
          >
            {contextLabel}
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
