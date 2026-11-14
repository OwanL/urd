import type { UsageBreakdown, UsagePoint, UsageRange, UsageTotals } from "./UsageTypes.ts";

export interface UsageRangeBounds {
  from: string;
  to: string;
}

export type CumulativeUsagePoint = UsagePoint;

export interface UsageItem {
  key: string;
  label: string;
  totals: UsageTotals;
}

export interface UsageProvider extends UsageItem {
  models: UsageItem[];
}

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

const purposeLabels = new Map([
  ["conversation", "Conversation"],
  ["compaction", "History compaction"],
  ["model-test", "Provider tests"],
]);

export function localUsageRange(now: Date, range: UsageRange): UsageRangeBounds {
  const start = new Date(now.getFullYear(), now.getMonth(), now.getDate());
  if (range === "sevenDays") start.setDate(start.getDate() - 6);
  return { from: start.toISOString(), to: now.toISOString() };
}

export function cumulativeUsageHistory(history: readonly UsagePoint[]): UsagePoint[] {
  const totals = emptyUsageTotals();
  const breakdownTotals = new Map<string, UsageBreakdown>();
  return history.map(({ at, totals: interval, breakdown }) => {
    addUsageTotals(totals, interval);
    for (const item of breakdown) {
      const key = breakdownKey(item);
      let cumulative = breakdownTotals.get(key);
      if (!cumulative) {
        cumulative = { ...item, totals: emptyUsageTotals() };
        breakdownTotals.set(key, cumulative);
      }
      addUsageTotals(cumulative.totals, item.totals);
    }
    return {
      at,
      totals: { ...totals },
      breakdown: Array.from(breakdownTotals.values(), (item) => ({
        ...item,
        totals: { ...item.totals },
      })),
    };
  });
}

export function usageProviders(breakdown: readonly UsageBreakdown[]): UsageProvider[] {
  const providers = new Map<
    string,
    { key: string; label: string; totals: UsageTotals; models: Map<string, UsageItem> }
  >();

  for (const item of breakdown) {
    const isOther = item.isOther === true;
    const providerKey = JSON.stringify(["provider", isOther, item.provider]);
    let provider = providers.get(providerKey);
    if (!provider) {
      provider = {
        key: providerKey,
        label: isOther ? "Other usage" : item.provider,
        totals: emptyUsageTotals(),
        models: new Map(),
      };
      providers.set(providerKey, provider);
    }
    addUsageTotals(provider.totals, item.totals);

    const modelKey = JSON.stringify(["model", providerKey, isOther, item.model]);
    let model = provider.models.get(modelKey);
    if (!model) {
      model = {
        key: modelKey,
        label: isOther ? "Other usage" : item.model,
        totals: emptyUsageTotals(),
      };
      provider.models.set(modelKey, model);
    }
    addUsageTotals(model.totals, item.totals);
  }

  return Array.from(providers.values(), (provider) => ({
    key: provider.key,
    label: provider.label,
    totals: { ...provider.totals },
    models: sortUsageItems(Array.from(provider.models.values())),
  })).sort(compareUsageItems);
}

export function usageSources(breakdown: readonly UsageBreakdown[]): UsageItem[] {
  const sources = new Map<string, UsageItem>();
  for (const item of breakdown) {
    const isOther = item.isOther === true;
    const key = JSON.stringify(["source", isOther, item.purpose]);
    let source = sources.get(key);
    if (!source) {
      source = {
        key,
        label: isOther ? "Other usage" : purposeLabel(item.purpose),
        totals: emptyUsageTotals(),
      };
      sources.set(key, source);
    }
    addUsageTotals(source.totals, item.totals);
  }
  return sortUsageItems(Array.from(sources.values()));
}

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

function breakdownKey(item: UsageBreakdown): string {
  return JSON.stringify([item.provider, item.model, item.purpose, item.isOther === true]);
}

function addUsageTotals(target: UsageTotals, addition: UsageTotals): void {
  target.costUsd += addition.costUsd;
  target.requests += addition.requests;
  target.estimatedRequests += addition.estimatedRequests;
  target.unknownRequests += addition.unknownRequests;
  target.activeRequests += addition.activeRequests;
}

function purposeLabel(purpose: string): string {
  const knownLabel = purposeLabels.get(purpose);
  if (knownLabel) return knownLabel;
  const readable = purpose
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .replace(/[-_]+/g, " ")
    .trim()
    .replace(/\s+/g, " ");
  if (!readable) return "Unknown purpose";
  return `${readable[0]!.toUpperCase()}${readable.slice(1)}`;
}

function sortUsageItems(items: UsageItem[]): UsageItem[] {
  return items.sort(compareUsageItems);
}

function compareUsageItems(left: UsageItem, right: UsageItem): number {
  if (left.totals.costUsd > right.totals.costUsd) return -1;
  if (left.totals.costUsd < right.totals.costUsd) return 1;
  return compareText(left.label, right.label) || compareText(left.key, right.key);
}

function compareText(left: string, right: string): number {
  return left < right ? -1 : left > right ? 1 : 0;
}
