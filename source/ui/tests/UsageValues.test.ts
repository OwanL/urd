import assert from "node:assert/strict";
import test from "node:test";
import type { UsagePoint, UsageTotals } from "../usage/UsageTypes.ts";
import {
  cumulativeUsageHistory,
  formatCostUsd,
  formatUsageCost,
  localUsageRange,
} from "../usage/UsageValues.ts";

const totals = (overrides: Partial<UsageTotals> = {}): UsageTotals => ({
  costUsd: 0,
  requests: 0,
  estimatedRequests: 0,
  unknownRequests: 0,
  activeRequests: 0,
  ...overrides,
});

void test("USD formatting preserves positive sub-cent costs and compact precision", () => {
  assert.equal(formatCostUsd(0), "$0.00");
  assert.equal(formatCostUsd(1.2), "$1.20");
  assert.equal(formatCostUsd(0.005), "$0.005");
  assert.equal(formatCostUsd(0.000012), "$0.000012");
  assert.equal(formatCostUsd(0.0000001), "<$0.000001");
  assert.equal(formatCostUsd(null), "—");
  assert.equal(formatUsageCost(totals({ requests: 2, unknownRequests: 2 })), "Unknown");
  assert.equal(formatUsageCost(totals()), "$0.00");
  assert.equal(
    formatUsageCost(totals({ costUsd: 0.005, unknownRequests: 2 })),
    "$0.005 + 2 unknown",
  );
  assert.equal(formatUsageCost(totals({ costUsd: 0.005, estimatedRequests: 1 })), "~$0.005");
});

void test("history accumulates returned interval costs and counts chronologically", () => {
  const history: UsagePoint[] = [
    {
      at: "2026-11-14T10:00:00Z",
      totals: totals({ costUsd: 0.01, requests: 2, estimatedRequests: 1, activeRequests: 1 }),
    },
    {
      at: "2026-11-14T11:00:00Z",
      totals: totals({ costUsd: 0.02, requests: 1, unknownRequests: 1 }),
    },
  ];
  assert.deepEqual(cumulativeUsageHistory(history), [
    {
      at: history[0]!.at,
      totals: totals({ costUsd: 0.01, requests: 2, estimatedRequests: 1, activeRequests: 1 }),
    },
    {
      at: history[1]!.at,
      totals: totals({
        costUsd: 0.03,
        requests: 3,
        estimatedRequests: 1,
        unknownRequests: 1,
        activeRequests: 1,
      }),
    },
  ]);
});

void test("local usage ranges start at local midnight for today or the preceding six days", () => {
  const now = new Date(2026, 10, 14, 15, 22, 8, 0);
  const today = localUsageRange(now, "today");
  const todayStart = new Date(today.from);
  assert.equal(todayStart.getFullYear(), now.getFullYear());
  assert.equal(todayStart.getMonth(), now.getMonth());
  assert.equal(todayStart.getDate(), now.getDate());
  assert.equal(todayStart.getHours(), 0);
  assert.equal(new Date(today.to).getTime(), now.getTime());

  const week = localUsageRange(now, "sevenDays");
  const weekStart = new Date(week.from);
  assert.equal(weekStart.getFullYear(), 2026);
  assert.equal(weekStart.getMonth(), 10);
  assert.equal(weekStart.getDate(), 8);
  assert.equal(weekStart.getHours(), 0);
});
