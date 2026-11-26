import type { UsageSummary, UsageTotals } from "./Usage.ts";

const usagePalette = [
  "#0891b2",
  "#16a34a",
  "#7c3aed",
  "#ea580c",
  "#2563eb",
  "#ca8a04",
  "#db2777",
  "#64748b",
];

export function usageColors(keys: readonly string[]): Record<string, string> {
  const uniqueKeys = Array.from(new Set(keys)).sort(compareText);
  const used = new Set<number>();
  return Object.fromEntries(
    uniqueKeys.map((key) => {
      let hash = 2166136261;
      for (const character of key) hash = Math.imul(hash ^ character.codePointAt(0)!, 16777619);
      let index = (hash >>> 0) % usagePalette.length;
      for (let offset = 0; offset < usagePalette.length && used.has(index); offset++)
        index = (index + 1) % usagePalette.length;
      used.add(index);
      return [key, usagePalette[index]!];
    }),
  );
}

export function formatCostUsd(costUsd: number | null): string {
  if (costUsd === null || !Number.isFinite(costUsd)) return "—";
  const absolute = Math.abs(costUsd);
  const sign = costUsd < 0 ? "−" : "";
  if (absolute > 0 && absolute < 0.01) return `${sign}<$0.01`;
  const formatted = absolute.toLocaleString("en-US", {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  });
  return `${sign}$${formatted}`;
}

export function formatUsageCost(totals: UsageTotals): string {
  if (totals.costUsd === 0 && totals.requests > 0 && totals.unknownRequests === totals.requests)
    return "—";
  return formatCostUsd(totals.costUsd);
}

// A closed session keeps only lifetime totals, so a partly overlapping range has no cost to show.
export function formatSummaryCost(summary: UsageSummary): string {
  return summary.totalsComplete === false ? "—" : formatUsageCost(summary.totals);
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

function compareText(left: string, right: string): number {
  return left < right ? -1 : left > right ? 1 : 0;
}
