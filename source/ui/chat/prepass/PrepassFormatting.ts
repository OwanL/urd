import { formatCostUsd } from "../../usage/UsagePresentation.ts";

export function formatClockTime(startedAt: string | null | undefined): string | null {
  const time = startedAt ? new Date(startedAt) : null;
  if (!time || Number.isNaN(time.getTime())) return null;
  return `${String(time.getHours()).padStart(2, "0")}:${String(time.getMinutes()).padStart(2, "0")}`;
}

// Elapsed preparation time ticks from the server's published start until its published finish.
export function formatPrepassElapsed(
  startedAt: string | null | undefined,
  finishedAt: string | null | undefined,
  now: number,
): string | null {
  const start = startedAt ? new Date(startedAt) : null;
  if (!start || Number.isNaN(start.getTime())) return null;
  const finish = finishedAt ? new Date(finishedAt) : null;
  const end = finish && !Number.isNaN(finish.getTime()) ? finish.getTime() : now;
  return formatElapsedMilliseconds(Math.max(0, end - start.getTime()));
}

export function formatElapsedMilliseconds(milliseconds: number): string | null {
  if (!Number.isFinite(milliseconds) || milliseconds < 0) return null;
  const seconds = Math.floor(milliseconds / 1_000);
  if (seconds < 60) return `${(milliseconds / 1_000).toFixed(1)} s`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m ${seconds % 60}s`;
  return `${Math.floor(minutes / 60)}h ${minutes % 60}m`;
}

// Costs stay truthful: pending work, absent totals and estimates are distinguishable.
export function prepassCostLabel(
  costUsd: number | null | undefined,
  costIsEstimated: boolean,
  finished: boolean,
): string {
  if (!finished) return "Cost pending";
  if (costUsd === null || costUsd === undefined || !Number.isFinite(costUsd)) return "—";
  const amount = formatCostUsd(costUsd);
  return costIsEstimated ? `~${amount}` : amount;
}
