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
      <UsageControl label="Today" range="today" busy={busy} refreshKey={refreshKey} />
      <UsageControl label="Last 7 days" range="sevenDays" busy={busy} refreshKey={refreshKey} />
    </div>
  );
}
