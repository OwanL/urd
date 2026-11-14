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
    <div class="usage-overview" aria-label="API cost totals">
      <div class="usage-overview-total">
        <span class="usage-overview-label" aria-hidden="true">
          24h API cost
        </span>
        <UsageControl label="24h" range="day" active={active} busy={busy} refreshKey={refreshKey} />
      </div>
      <div class="usage-overview-total">
        <span class="usage-overview-label" aria-hidden="true">
          7d API cost
        </span>
        <UsageControl
          label="7d"
          range="sevenDays"
          active={active}
          busy={busy}
          refreshKey={refreshKey}
        />
      </div>
    </div>
  );
}
