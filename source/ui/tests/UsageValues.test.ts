import assert from "node:assert/strict";
import test from "node:test";
import type { UsageBreakdown, UsagePoint, UsageTotals } from "../usage/UsageTypes.ts";
import {
  cumulativeUsageHistory,
  formatCostUsd,
  formatUsageCost,
  usageRange,
  usageColors,
  usageProviders,
  usageSources,
} from "../usage/UsageValues.ts";

const totals = (overrides: Partial<UsageTotals> = {}): UsageTotals => ({
  costUsd: 0,
  requests: 0,
  estimatedRequests: 0,
  unknownRequests: 0,
  activeRequests: 0,
  ...overrides,
});

const breakdown = (
  provider: string,
  model: string,
  purpose: string,
  usageTotals: Partial<UsageTotals> = {},
  isOther?: boolean,
): UsageBreakdown => ({
  provider,
  model,
  purpose,
  totals: totals(usageTotals),
  ...(isOther === undefined ? {} : { isOther }),
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
      breakdown: [],
    },
    {
      at: "2026-11-14T11:00:00Z",
      totals: totals({ costUsd: 0.02, requests: 1, unknownRequests: 1 }),
      breakdown: [],
    },
  ];
  assert.deepEqual(cumulativeUsageHistory(history), [
    {
      at: history[0]!.at,
      totals: totals({ costUsd: 0.01, requests: 2, estimatedRequests: 1, activeRequests: 1 }),
      breakdown: [],
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
      breakdown: [],
    },
  ]);
});

void test("cumulative history snapshots totals by full breakdown identity without aliasing", () => {
  const history: UsagePoint[] = [
    {
      at: "2026-11-14T10:00:00Z",
      totals: totals({
        costUsd: 0.4,
        requests: 2,
        estimatedRequests: 1,
        unknownRequests: 1,
        activeRequests: 1,
      }),
      breakdown: [
        breakdown("Alpha", "Model X", "conversation", {
          costUsd: 0.1,
          requests: 1,
          estimatedRequests: 1,
        }),
        breakdown("Alpha", "Model X", "compaction", {
          costUsd: 0.2,
          requests: 1,
          activeRequests: 1,
        }),
        breakdown("", "", "", { costUsd: 0.1, requests: 1, unknownRequests: 1 }, true),
      ],
    },
    {
      at: "2026-11-14T11:00:00Z",
      totals: totals({
        costUsd: 0.6,
        requests: 2,
        estimatedRequests: 1,
        unknownRequests: 1,
        activeRequests: 1,
      }),
      breakdown: [
        breakdown("Alpha", "Model X", "conversation", {
          costUsd: 0.3,
          requests: 2,
          estimatedRequests: 1,
          activeRequests: 1,
        }),
        breakdown("", "", "", { costUsd: 0.1, requests: 1, unknownRequests: 1 }, true),
        breakdown("Alpha", "Model X", "model-test", {
          costUsd: 0.2,
          requests: 1,
          unknownRequests: 1,
        }),
      ],
    },
  ];

  const cumulative = cumulativeUsageHistory(history);
  assert.deepEqual(
    cumulative[1]!.totals,
    totals({
      costUsd: 1,
      requests: 4,
      estimatedRequests: 2,
      unknownRequests: 2,
      activeRequests: 2,
    }),
  );
  assert.deepEqual(cumulative[1]!.breakdown, [
    breakdown("Alpha", "Model X", "conversation", {
      costUsd: 0.4,
      requests: 3,
      estimatedRequests: 2,
      activeRequests: 1,
    }),
    breakdown("Alpha", "Model X", "compaction", {
      costUsd: 0.2,
      requests: 1,
      activeRequests: 1,
    }),
    breakdown("", "", "", { costUsd: 0.2, requests: 2, unknownRequests: 2 }, true),
    breakdown("Alpha", "Model X", "model-test", {
      costUsd: 0.2,
      requests: 1,
      unknownRequests: 1,
    }),
  ]);
  assert.notStrictEqual(cumulative[0]!.breakdown, cumulative[1]!.breakdown);
  assert.notStrictEqual(cumulative[0]!.breakdown[0], cumulative[1]!.breakdown[0]);
  assert.notStrictEqual(cumulative[0]!.breakdown[0]!.totals, cumulative[1]!.breakdown[0]!.totals);
  cumulative[0]!.breakdown[0]!.totals.costUsd = 99;
  assert.equal(cumulative[1]!.breakdown[0]!.totals.costUsd, 0.4);
  assert.equal(history[0]!.breakdown[0]!.totals.costUsd, 0.1);
});

void test("usage providers and sources aggregate costs, counts, labels and purposes", () => {
  const breakdowns = [
    breakdown("Provider B", "Model X", "conversation", { costUsd: 0.3, requests: 1 }),
    breakdown("Provider A", "Shared model", "compaction", { costUsd: 0.2, requests: 1 }),
    breakdown("Provider A", "Shared model", "model-test", {
      costUsd: 0.1,
      requests: 1,
      unknownRequests: 1,
    }),
    breakdown("Provider A", "Custom model", "custom-purpose", {
      costUsd: 0.25,
      requests: 1,
      estimatedRequests: 2,
      unknownRequests: 3,
      activeRequests: 4,
    }),
    breakdown("Provider B", "Another model", "custom-purpose", {
      costUsd: 0.05,
      requests: 2,
      estimatedRequests: 3,
      unknownRequests: 4,
      activeRequests: 6,
    }),
    breakdown("", "", "", { costUsd: 0.15, requests: 1, unknownRequests: 1 }, true),
  ];

  const providers = usageProviders(breakdowns);
  assert.deepEqual(
    providers.map(({ label, totals: itemTotals }) => [label, itemTotals.costUsd]),
    [
      ["Provider A", 0.55],
      ["Provider B", 0.35],
      ["Other usage", 0.15],
    ],
  );
  assert.deepEqual(
    providers[0]!.models.map(({ label, totals: itemTotals }) => [label, itemTotals.costUsd]),
    [
      ["Shared model", 0.30000000000000004],
      ["Custom model", 0.25],
    ],
  );
  assert.deepEqual(
    providers[0]!.models[0]!.totals,
    totals({
      costUsd: 0.30000000000000004,
      requests: 2,
      unknownRequests: 1,
    }),
  );
  assert.equal(providers[2]!.models[0]!.label, "Other usage");
  assert.equal(new Set(providers.map(({ key }) => key)).size, providers.length);

  const sources = usageSources(breakdowns);
  assert.deepEqual(
    sources.map(({ label, totals: itemTotals }) => [label, itemTotals.costUsd]),
    [
      ["Conversation", 0.3],
      ["Custom purpose", 0.3],
      ["History compaction", 0.2],
      ["Other usage", 0.15],
      ["Provider tests", 0.1],
    ],
  );
  assert.deepEqual(
    sources[1]!.totals,
    totals({
      costUsd: 0.3,
      requests: 3,
      estimatedRequests: 5,
      unknownRequests: 7,
      activeRequests: 10,
    }),
  );
  assert.equal(new Set(sources.map(({ key }) => key)).size, sources.length);
});

void test("usage colors are deterministic, order-independent and distinct for small sets", () => {
  const keys = ["provider-a", "source-conversation", "source-other", "provider-b"];
  const colors = usageColors(keys);
  assert.deepEqual(usageColors([...keys].reverse()), colors);
  assert.equal(new Set(Object.values(colors)).size, keys.length);
  assert.equal(usageColors([keys[0]!])[keys[0]!], colors[keys[0]!]);
  assert.equal(Object.hasOwn(usageColors(["__proto__"]), "__proto__"), true);
});

void test("usage ranges cover rolling 24 and 168 hours across calendar and DST boundaries", () => {
  const cases = [
    {
      now: "2026-01-01T00:15:00.123Z",
      day: "2025-12-31T00:15:00.123Z",
      week: "2025-12-25T00:15:00.123Z",
    },
    {
      now: "2026-03-08T03:30:00-04:00",
      day: "2026-03-07T07:30:00.000Z",
      week: "2026-03-01T07:30:00.000Z",
    },
    {
      now: "2026-11-01T01:30:00-05:00",
      day: "2026-10-31T06:30:00.000Z",
      week: "2026-10-25T06:30:00.000Z",
    },
  ];
  for (const { now, day, week } of cases) {
    const instant = new Date(now);
    assert.deepEqual(usageRange(instant, "day"), { from: day, to: instant.toISOString() });
    assert.deepEqual(usageRange(instant, "sevenDays"), { from: week, to: instant.toISOString() });
    assert.equal(instant.toISOString(), new Date(now).toISOString());
  }
});
