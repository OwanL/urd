import assert from "node:assert/strict";
import test from "node:test";
import {
  effortLabel,
  formatClockTime,
  formatElapsedMilliseconds,
  formatPrepassElapsed,
  modelDisplayName,
  orderedPreparation,
  preparationOutcome,
  preparationUsedSessionModel,
  prepassCostLabel,
  prepassRowModel,
  prepassRowMood,
  uniqueModelNames,
} from "../app/PrepassValues.ts";
import { initialBoundaryId } from "../sessions/Prepass.ts";
import type { PrepassSummary } from "../sessions/Prepass.ts";

function summary(overrides: Partial<PrepassSummary>): PrepassSummary {
  return {
    boundaryId: "80000000-0000-0000-0000-000000000008",
    category: "tools",
    state: "completed",
    candidateCount: null,
    resultCount: null,
    ...overrides,
  };
}

void test("preparation rows keep category order and unknown categories", () => {
  assert.deepEqual(
    orderedPreparation([
      summary({ category: "instructions" }),
      summary({ category: "title", state: "running" }),
      summary({ category: "skills" }),
      summary({ category: "tools" }),
    ]).map((row) => row.category),
    ["tools", "skills", "instructions", "title"],
  );
  assert.deepEqual(orderedPreparation(undefined), []);
  assert.deepEqual(orderedPreparation(null), []);
  assert.deepEqual(orderedPreparation([]), []);
});

void test("preparation collapses only after every category settles", () => {
  const ready = ["tools", "skills", "instructions", "title"].map((category) =>
    summary({ category }),
  );
  assert.equal(preparationOutcome(ready), "Complete");
  assert.equal(preparationOutcome([]), null);
  for (const state of ["running", "pending", "unknown"]) {
    assert.equal(
      preparationOutcome([...ready.slice(0, 3), summary({ category: "title", state })]),
      null,
      state,
    );
  }
});

void test("settled preparation issues remain explicit in the collapsed summary", () => {
  for (const state of ["fallback", "failed", "cancelled", "interrupted"]) {
    assert.equal(
      preparationOutcome([summary({}), summary({ category: "skills", state })]),
      "Finished with issues",
      state,
    );
  }
});

void test("completed rows show pruned candidate shares", () => {
  assert.equal(
    prepassRowModel(summary({ state: "completed", candidateCount: 2, resultCount: 0 })).value,
    "2/2 pruned",
  );
  assert.equal(
    prepassRowModel(summary({ state: "completed", candidateCount: 1, resultCount: 1 })).value,
    "0/1 pruned",
  );
  // A zero/zero outcome is a valid label, not fake progress.
  assert.equal(
    prepassRowModel(summary({ state: "completed", candidateCount: 0, resultCount: 0 })).value,
    "0/0 pruned",
  );
  assert.equal(
    prepassRowModel(summary({ state: "completed", candidateCount: null, resultCount: null })).value,
    "Completed",
  );
});

void test("running rows count candidates without fabricating zero progress", () => {
  const running = summary({ state: "running", candidateCount: 2, resultCount: null });
  assert.equal(prepassRowModel(running).value, "Reviewing 2 candidates");
  assert.equal(prepassRowMood(running), "running");
  assert.equal(
    prepassRowModel(summary({ state: "running", candidateCount: 1, resultCount: null })).value,
    "Reviewing 1 candidate",
  );
  assert.equal(
    prepassRowModel(summary({ state: "running", candidateCount: 0, resultCount: null })).value,
    "Running",
  );
  assert.equal(
    prepassRowModel(summary({ state: "running", candidateCount: null, resultCount: null })).value,
    "Running",
  );
});

void test("title rows use their own progress wording", () => {
  assert.equal(
    prepassRowModel(summary({ category: "title", state: "running", candidateCount: 2 })).value,
    "Generating",
  );
  assert.equal(
    prepassRowModel(
      summary({ category: "title", state: "completed", candidateCount: 1, resultCount: 1 }),
    ).value,
    "Complete",
  );
});

void test("fallback rows read as failed selection that kept every candidate", () => {
  assert.equal(prepassRowModel(summary({ state: "fallback" })).value, "Failed · kept all");
  assert.equal(prepassRowMood(summary({ state: "fallback" })), "warned");
  assert.equal(prepassRowModel(summary({ state: "cancelled" })).value, "Cancelled");
  assert.equal(prepassRowModel(summary({ state: "interrupted" })).value, "Interrupted");
  assert.equal(prepassRowMood(summary({ state: "interrupted" })), "stopped");
  assert.equal(prepassRowModel(summary({ state: "pending" })).value, "Pending");
  assert.equal(
    prepassRowModel(summary({ state: "deferred", candidateCount: 3, resultCount: 1 })).value,
    "Deferred",
  );
});

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
  assert.equal(prepassCostLabel(0.0003, true, true), "~$0.0003");
  assert.equal(prepassCostLabel(1.25, false, true), "$1.25");
  assert.equal(prepassCostLabel(null, false, true), "—");
});

void test("model names only use an unambiguous catalog cache", () => {
  const options = [
    { modelId: "gpt-5-mini", modelLabel: "GPT-5 mini" },
    { modelId: "gpt-5.1", modelLabel: "GPT-5.1" },
  ];
  assert.deepEqual(
    [...uniqueModelNames(options)],
    [
      ["gpt-5-mini", "GPT-5 mini"],
      ["gpt-5.1", "GPT-5.1"],
    ],
  );
  const conflict = [
    { modelId: "shared", modelLabel: "First" },
    { modelId: "shared", modelLabel: "Second" },
  ];
  assert.deepEqual([...uniqueModelNames(conflict)], []);
  assert.deepEqual([...uniqueModelNames([])], []);
  assert.equal(modelDisplayName("gpt-5-mini", uniqueModelNames(options)), "GPT-5 mini");
  assert.equal(modelDisplayName("unknown", uniqueModelNames(options)), "unknown");
  assert.equal(modelDisplayName("gpt-5-mini", null), "gpt-5-mini");
  assert.equal(modelDisplayName("", uniqueModelNames(options)), null);
  assert.equal(modelDisplayName(null, uniqueModelNames(options)), null);
});

void test("reasoning effort passes through verbatim or stays hidden", () => {
  assert.equal(effortLabel("medium"), "medium");
  assert.equal(effortLabel("extraHigh"), "extraHigh");
  assert.equal(effortLabel(null), null);
  assert.equal(effortLabel(undefined), null);
  assert.equal(effortLabel(""), null);
});

void test("the chat-model fallback warns only on the initial boundary", () => {
  assert.equal(
    preparationUsedSessionModel([
      summary({ boundaryId: initialBoundaryId, usedSessionModel: true }),
      summary({ boundaryId: initialBoundaryId, category: "skills" }),
    ]),
    true,
  );
  // Compaction boundaries never warn, even when a summary used the chat model.
  assert.equal(preparationUsedSessionModel([summary({ usedSessionModel: true })]), false);
  assert.equal(preparationUsedSessionModel([summary({ boundaryId: initialBoundaryId })]), false);
  assert.equal(preparationUsedSessionModel([]), false);
  assert.equal(preparationUsedSessionModel(null), false);
  assert.equal(preparationUsedSessionModel(undefined), false);
});
