import assert from "node:assert/strict";
import test from "node:test";
import type { UsageTotals } from "../../usage/Usage.ts";
import { formatCostUsd, formatUsageCost, usageColors } from "../../usage/UsagePresentation.ts";

const totals = (overrides: Partial<UsageTotals> = {}): UsageTotals => ({
  costUsd: 0,
  requests: 0,
  estimatedRequests: 0,
  unknownRequests: 0,
  activeRequests: 0,
  ...overrides,
});

void test("USD formatting uses two decimals and distinguishes sub-cent costs from zero", () => {
  assert.equal(formatCostUsd(0), "$0.00");
  assert.equal(formatCostUsd(1.2), "$1.20");
  assert.equal(formatCostUsd(0.126), "$0.13");
  assert.equal(formatCostUsd(0.01), "$0.01");
  assert.equal(formatCostUsd(0.005), "<$0.01");
  assert.equal(formatCostUsd(0.0000001), "<$0.01");
  assert.equal(formatCostUsd(-0.005), "−<$0.01");
  assert.equal(formatCostUsd(null), "—");
  assert.equal(formatUsageCost(totals({ requests: 2, unknownRequests: 2 })), "Unknown");
  assert.equal(formatUsageCost(totals()), "$0.00");
  assert.equal(
    formatUsageCost(totals({ costUsd: 0.005, unknownRequests: 2 })),
    "<$0.01 + 2 unknown",
  );
  assert.equal(formatUsageCost(totals({ costUsd: 0.005, estimatedRequests: 1 })), "~<$0.01");
});

void test("usage colors are deterministic, order-independent and distinct for small sets", () => {
  const keys = ["provider-a", "source-conversation", "source-other", "provider-b"];
  const colors = usageColors(keys);
  assert.deepEqual(usageColors([...keys].reverse()), colors);
  assert.equal(new Set(Object.values(colors)).size, keys.length);
  assert.equal(usageColors([keys[0]!])[keys[0]!], colors[keys[0]!]);
  assert.equal(Object.hasOwn(usageColors(["__proto__"]), "__proto__"), true);
});
