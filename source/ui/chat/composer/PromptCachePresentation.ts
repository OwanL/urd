import type { PromptCacheTotals } from "../../usage/Usage.ts";
import { formatUsageCount } from "../../usage/UsagePresentation.ts";

export function promptCacheText(totals: PromptCacheTotals | null): string {
  if (!totals) return "No completed turn";
  if (totals.totalRequests === 0) return "No recorded requests";
  if (totals.reportedRequests === 0) return "Not reported";

  const uncached = totals.inputTokens - totals.cachedInputTokens;
  const partial = totals.reportedRequests < totals.totalRequests ? " (partial)" : "";
  return `${formatCacheShare(totals.cachedInputTokens, totals.inputTokens)} cached · ${formatUsageCount(uncached)} uncached${partial}`;
}

export function formatCacheShare(cached: number, input: number): string {
  if (cached === 0) return "0%";
  if (cached === input) return "100%";
  const percent = ((cached / input) * 100).toFixed(1);
  if (percent === "0.0") return "<0.1%";
  if (percent === "100.0") return ">99.9%";
  return `${percent}%`;
}
