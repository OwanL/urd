import type { UsagePoint, UsageRange, UsageTotals } from "./UsageTypes.ts";

export interface UsageRangeBounds {
  from: string;
  to: string;
}

export interface CumulativeUsagePoint {
  at: string;
  totals: UsageTotals;
}

export function localUsageRange(now: Date, range: UsageRange): UsageRangeBounds {
  const start = new Date(now.getFullYear(), now.getMonth(), now.getDate());
  if (range === "sevenDays") start.setDate(start.getDate() - 6);
  return { from: start.toISOString(), to: now.toISOString() };
}

export function cumulativeUsageHistory(history: readonly UsagePoint[]): CumulativeUsagePoint[] {
  const totals = emptyUsageTotals();
  return history.map(({ at, totals: interval }) => {
    totals.costUsd += interval.costUsd;
    totals.requests += interval.requests;
    totals.estimatedRequests += interval.estimatedRequests;
    totals.unknownRequests += interval.unknownRequests;
    totals.activeRequests += interval.activeRequests;
    return { at, totals: { ...totals } };
  });
}

export function emptyUsageTotals(): UsageTotals {
  return {
    costUsd: 0,
    requests: 0,
    estimatedRequests: 0,
    unknownRequests: 0,
    activeRequests: 0,
  };
}

export function formatCostUsd(costUsd: number | null): string {
  if (costUsd === null || !Number.isFinite(costUsd)) return "—";
  if (costUsd === 0) return "$0.00";
  const absolute = Math.abs(costUsd);
  if (absolute < 0.0000005) return `${costUsd < 0 ? "−" : ""}<$0.000001`;
  const decimals = absolute >= 1 ? 2 : 6;
  const formatted = absolute.toLocaleString("en-US", {
    minimumFractionDigits: 2,
    maximumFractionDigits: decimals,
  });
  return `${costUsd < 0 ? "−" : ""}$${formatted}`;
}

export function formatUsageCost(totals: UsageTotals): string {
  if (totals.costUsd === 0 && totals.requests > 0 && totals.unknownRequests === totals.requests)
    return "Unknown";
  const estimate = totals.estimatedRequests > 0 ? "~" : "";
  const unknown = totals.unknownRequests > 0 ? ` + ${totals.unknownRequests} unknown` : "";
  return `${estimate}${formatCostUsd(totals.costUsd)}${unknown}`;
}

export function formatUsageCount(count: number): string {
  return Number.isFinite(count) && count >= 0 ? Math.floor(count).toLocaleString("en-US") : "?";
}

export function formatUsageTime(value: string): string {
  const date = new Date(value);
  if (!Number.isFinite(date.getTime())) return "Unknown time";
  return new Intl.DateTimeFormat(undefined, {
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit",
  }).format(date);
}
