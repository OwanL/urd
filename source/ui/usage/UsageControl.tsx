import { useMemo } from "preact/hooks";
import type { UsageRange } from "./Usage.ts";
import { UsageClient } from "./UsageClient.ts";
import { UsageDetails } from "./UsageDetails.tsx";
import { HoverPopover } from "../controls/HoverPopover.tsx";
import { formatSummaryCost } from "./UsagePresentation.ts";
import { useUsageSummary } from "./useUsageSummary.ts";
import "./usage.css";

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
  const state = useUsageSummary({
    client,
    enabled: active,
    pollMilliseconds: busy ? 1_000 : 15_000,
    refreshKey,
    ...(sessionId ? { sessionId } : { range: range ?? "day" }),
    detail: false,
  });
  const value = state.summary
    ? formatSummaryCost(state.summary)
    : state.stale
      ? "Unavailable"
      : "…";
  const staleLabel = state.stale ? (state.summary ? ", stale" : ", unavailable") : "";
  const scope = sessionId ? "Session" : label;
  return (
    <HoverPopover
      label={`${scope} API-equivalent cost: ${value}${staleLabel}`}
      surfaceLabel={`Usage details: ${sessionId ? "this session" : label}`}
      class={`usage-control${className ? ` ${className}` : ""}`}
      surfaceClass="usage-popover"
      enabled={active}
      content={
        <UsageDetails
          key={sessionId ?? range ?? "day"}
          client={client}
          {...(sessionId ? { sessionId } : { range: range ?? "day" })}
        />
      }
    >
      <span class="usage-control-value">{value}</span>
      {state.stale && (
        <span class="usage-stale-mark" aria-hidden="true">
          · stale
        </span>
      )}
    </HoverPopover>
  );
}
