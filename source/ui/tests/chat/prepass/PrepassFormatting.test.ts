import assert from "node:assert/strict";
import test from "node:test";
import {
  formatClockTime,
  formatElapsedMilliseconds,
  formatPrepassElapsed,
  prepassCostLabel,
} from "../../../chat/prepass/PrepassFormatting.ts";

void test("clock time drops seconds from the started time", () => {
  const local = new Date(2026, 10, 17, 14, 32, 9);
  assert.equal(formatClockTime(local.toISOString()), "14:32");
  assert.equal(formatClockTime(null), null);
  assert.equal(formatClockTime("not a time"), null);
  assert.equal(formatClockTime(""), null);
});

void test("elapsed runs from the published start to the published finish", () => {
  const started = "2026-10-12T12:00:00.000Z";
  assert.equal(formatPrepassElapsed(started, "2026-10-12T12:00:01.800Z", Date.now()), "1.8 s");
  assert.equal(formatPrepassElapsed(started, null, Date.parse(started) + 10_000), "10.0 s");
  assert.equal(formatPrepassElapsed(started, null, Date.parse(started) + 65_000), "1m 5s");
  assert.equal(formatElapsedMilliseconds(3_660_000), "1h 1m");
  assert.equal(formatPrepassElapsed(null, null, Date.now()), null);
  assert.equal(formatPrepassElapsed("broken", null, Date.now()), null);
  // A finished time never regresses to a later local tick.
  assert.equal(
    formatPrepassElapsed(started, "2026-10-12T12:00:02.000Z", Date.parse("2027-01-01T00:00:00Z")),
    "2.0 s",
  );
});

void test("costs distinguish pending, estimated, exact and absent totals", () => {
  assert.equal(prepassCostLabel(0.0003, true, false), "Cost pending");
  assert.equal(prepassCostLabel(null, false, false), "Cost pending");
  assert.equal(prepassCostLabel(0.0003, true, true), "~<$0.01");
  assert.equal(prepassCostLabel(1.25, false, true), "$1.25");
  assert.equal(prepassCostLabel(1, false, true), "$1.00");
  assert.equal(prepassCostLabel(null, false, true), "—");
  assert.equal(prepassCostLabel(Number.NaN, false, true), "—");
});
