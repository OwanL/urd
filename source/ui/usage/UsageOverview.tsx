import { UsageControl } from "./UsageControl.tsx";

export function UsageOverview({
  busy = false,
  refreshKey = 0,
}: {
  busy?: boolean;
  refreshKey?: number;
}) {
  return (
    <div class="usage-overview" aria-label="Usage totals">
      <div class="usage-overview-total">
        <span class="usage-overview-label" aria-hidden="true">
          Today
        </span>
        <UsageControl label="Today" range="today" busy={busy} refreshKey={refreshKey} />
      </div>
      <div class="usage-overview-total">
        <span class="usage-overview-label" aria-hidden="true">
          7d
        </span>
        <UsageControl label="Last 7 days" range="sevenDays" busy={busy} refreshKey={refreshKey} />
      </div>
    </div>
  );
}
