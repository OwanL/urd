import { UsageControl } from "./UsageControl.tsx";

export function UsageOverview({
  active = true,
  busy = false,
  refreshKey = 0,
}: {
  active?: boolean;
  busy?: boolean;
  refreshKey?: number | string;
}) {
  return (
    <div class="usage-overview" aria-label="Usage totals">
      <div class="usage-overview-total">
        <span class="usage-overview-label" aria-hidden="true">
          Today
        </span>
        <UsageControl
          label="Today"
          range="today"
          active={active}
          busy={busy}
          refreshKey={refreshKey}
        />
      </div>
      <div class="usage-overview-total">
        <span class="usage-overview-label" aria-hidden="true">
          Last 7 days
        </span>
        <UsageControl
          label="Last 7 days"
          range="sevenDays"
          active={active}
          busy={busy}
          refreshKey={refreshKey}
        />
      </div>
    </div>
  );
}
