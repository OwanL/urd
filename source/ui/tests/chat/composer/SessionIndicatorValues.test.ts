import assert from "node:assert/strict";
import { test } from "node:test";
import {
  formatTokenCount,
  formatWorkingTime,
  workingMilliseconds,
} from "../../../chat/composer/SessionIndicatorValues.ts";

void test("context stays compact and distinguishes unknown from zero", () => {
  assert.equal(formatTokenCount(null), "?");
  assert.equal(formatTokenCount(0), "0");
  assert.equal(formatTokenCount(999), "999");
  assert.equal(formatTokenCount(123_456), "123.5k");
  assert.equal(formatTokenCount(200_000), "200k");
});

void test("working time adds only the active interval and settles without ticking", () => {
  const time = { completedMilliseconds: 65_000, startedAt: null, hasUnknownDuration: false };
  const now = Date.parse("2026-11-12T12:00:10Z");
  assert.equal(workingMilliseconds(time, now), 65_000);
  assert.equal(workingMilliseconds({ ...time, startedAt: "2026-11-12T12:00:00Z" }, now), 75_000);
  assert.equal(workingMilliseconds({ ...time, startedAt: "2026-11-12T12:00:20Z" }, now), 65_000);
  assert.equal(formatWorkingTime(999), "0s");
  assert.equal(formatWorkingTime(75_000), "1m 15s");
  assert.equal(formatWorkingTime(3_725_000), "1h 2m");
});
