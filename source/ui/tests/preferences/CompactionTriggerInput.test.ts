import assert from "node:assert/strict";
import test from "node:test";
import {
  compactionSliderPosition,
  compactionTriggerInput,
  defaultCompactionTriggerTokenCount,
  maximumCompactionTriggerTokenCount,
  reconcileCompactionTriggerDraft,
  parseCompactionTriggerInput,
} from "../../preferences/CompactionTriggerInput.ts";

void test("compaction trigger input maps blank to the default and preserves valid integer values", () => {
  assert.deepEqual(parseCompactionTriggerInput(""), { valid: true, value: null });
  assert.deepEqual(parseCompactionTriggerInput("1"), { valid: true, value: 1 });
  assert.deepEqual(parseCompactionTriggerInput("4096"), { valid: true, value: 4_096 });
  assert.deepEqual(parseCompactionTriggerInput(String(maximumCompactionTriggerTokenCount)), {
    valid: true,
    value: maximumCompactionTriggerTokenCount,
  });
  assert.equal(compactionTriggerInput(null), "");
  assert.equal(compactionTriggerInput(4_096), "4096");
});

void test("compaction slider fits the default and rounds only the thumb for exact overrides", () => {
  assert.equal(compactionSliderPosition(defaultCompactionTriggerTokenCount), 150_000);
  for (const [exact, thumb] of [
    [5_000, 40_000],
    [40_000, 40_000],
    [45_000, 45_000],
    [78_123, 80_000],
    [2_147_483_647, 300_000],
  ] as const) {
    assert.equal(compactionSliderPosition(exact), thumb);
    assert.deepEqual(parseCompactionTriggerInput(compactionTriggerInput(exact)), {
      valid: true,
      value: exact,
    });
  }
});

void test("compaction trigger refresh preserves edits but follows the latest saved value after reverting", () => {
  const edited = reconcileCompactionTriggerDraft("8192", true, 4_096);
  assert.deepEqual(edited, { value: "8192", edited: true });

  const reverted = reconcileCompactionTriggerDraft("4096", true, 4_096);
  assert.deepEqual(reverted, { value: "4096", edited: false });
  assert.deepEqual(reconcileCompactionTriggerDraft(reverted.value, reverted.edited, 8_192), {
    value: "8192",
    edited: false,
  });
});

void test("compaction trigger input rejects non-integers and values outside the API range", () => {
  const invalid = parseCompactionTriggerInput("1.5");
  assert.equal(invalid.valid, false);
  if (!invalid.valid) assert.match(invalid.error, /whole number/);

  for (const value of ["0", "-1", "1e3", "2147483648", " "]) {
    assert.equal(parseCompactionTriggerInput(value).valid, false, value);
  }
});
