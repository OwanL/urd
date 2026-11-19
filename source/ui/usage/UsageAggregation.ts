import type { UsageBreakdown, UsagePoint, UsageRange, UsageTotals } from "./Usage.ts";

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

const purposeLabels = new Map([
  ["conversation", "Conversation"],
  ["compaction", "History compaction"],
  ["model-test", "Provider tests"],
]);

export function usageRange(now: Date, range: UsageRange): UsageRangeBounds {
  const hours = range === "sevenDays" ? 168 : 24;
  return {
    from: new Date(now.getTime() - hours * 60 * 60 * 1000).toISOString(),
    to: now.toISOString(),
  };
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

export function emptyUsageTotals(): UsageTotals {
  return {
    costUsd: 0,
    requests: 0,
    estimatedRequests: 0,
    unknownRequests: 0,
    activeRequests: 0,
  };
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
