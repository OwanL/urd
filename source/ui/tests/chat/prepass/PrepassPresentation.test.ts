import assert from "node:assert/strict";
import test from "node:test";
import {
  effortLabel,
  modelDisplayName,
  orderedPrepass,
  prepassOutcome,
  prepassUsedSessionModel,
  prepassRowModel,
  prepassRowMood,
  unanchoredPrepass,
  uniqueModelNames,
} from "../../../chat/prepass/PrepassPresentation.ts";
import { initialBoundaryId } from "../../../sessions/transcript/Prepass.ts";
import type { PrepassSummary } from "../../../sessions/transcript/Prepass.ts";
import type { TranscriptEntryDescriptor } from "../../../sessions/transcript/Messages.ts";

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

void test("after compaction, the loaded marker owns its running preparation rows", () => {
  const skills = summary({ category: "skills", state: "running" });
  const tools = summary({});
  const initialSkills = summary({ boundaryId: initialBoundaryId, category: "skills" });
  const marker: TranscriptEntryDescriptor = {
    entry: {
      executionId: "compact",
      entryId: skills.boundaryId,
      kind: "compaction",
      historyRevision: 1,
    },
    createdAt: null,
    text: null,
    tool: null,
    preparation: [{ ...skills }],
  };
  const pending = [skills, tools, initialSkills];

  assert.deepEqual(unanchoredPrepass(pending, [marker], 1), [tools, initialSkills]);
  assert.deepEqual(unanchoredPrepass([skills], [marker], 1), []);
  assert.deepEqual(unanchoredPrepass(pending, [], 1), pending);
  assert.deepEqual(unanchoredPrepass(pending, [{ ...marker, preparation: [] }], 1), pending);
  assert.deepEqual(unanchoredPrepass(pending, [marker], 2), pending);
});

void test("preparation rows keep category order and unknown categories", () => {
  assert.deepEqual(
    orderedPrepass([
      summary({ category: "instructions" }),
      summary({ category: "title", state: "running" }),
      summary({ category: "skills" }),
      summary({ category: "tools" }),
    ]).map((row) => row.category),
    ["tools", "skills", "instructions", "title"],
  );
  assert.deepEqual(orderedPrepass(undefined), []);
  assert.deepEqual(orderedPrepass(null), []);
  assert.deepEqual(orderedPrepass([]), []);
});

void test("preparation collapses only after every category settles", () => {
  const ready = ["tools", "skills", "instructions", "title"].map((category) =>
    summary({ category }),
  );
  assert.equal(prepassOutcome(ready), "Complete");
  assert.equal(prepassOutcome([]), null);
  for (const state of ["running", "pending", "unknown"]) {
    assert.equal(
      prepassOutcome([...ready.slice(0, 3), summary({ category: "title", state })]),
      null,
      state,
    );
  }
});

void test("settled preparation issues remain explicit in the collapsed summary", () => {
  for (const state of ["fallback", "failed", "cancelled", "interrupted"]) {
    assert.equal(
      prepassOutcome([summary({}), summary({ category: "skills", state })]),
      "Finished with issues",
      state,
    );
  }
});

void test("instruction errors remain warnings even when running or completed", () => {
  for (const state of ["running", "completed"]) {
    const row = summary({ category: "instructions", state, error: "Instructions omitted" });
    assert.equal(prepassRowMood(row), "warned");
    assert.equal(prepassOutcome([row]), state === "running" ? null : "Finished with issues");
  }
  assert.equal(prepassRowMood(summary({ category: "instructions", error: null })), "settled");
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

void test("fallback rows report retained resources without claiming unprotected resources", () => {
  assert.equal(
    prepassRowModel(summary({ category: "skills", state: "fallback" })).value,
    "Failed · protected only",
  );
  assert.equal(
    prepassRowModel(summary({ category: "instructions", state: "fallback" })).value,
    "Failed · protected only",
  );
  assert.equal(prepassRowModel(summary({ state: "fallback" })).value, "Failed · kept all");
  assert.equal(prepassRowMood(summary({ state: "fallback" })), "warned");
  assert.equal(
    prepassRowModel(
      summary({ category: "skills", state: "fallback", candidateCount: 4, resultCount: 2 }),
    ).value,
    "Failed · retained 2/4",
  );
  assert.equal(
    prepassRowModel(
      summary({ category: "instructions", state: "fallback", candidateCount: 4, resultCount: 4 }),
    ).value,
    "Failed · retained 4/4",
  );
  assert.equal(prepassRowModel(summary({ state: "cancelled" })).value, "Cancelled");
  assert.equal(prepassRowModel(summary({ state: "interrupted" })).value, "Interrupted");
  assert.equal(prepassRowMood(summary({ state: "interrupted" })), "stopped");
  assert.equal(prepassRowModel(summary({ state: "pending" })).value, "Pending");
  assert.equal(
    prepassRowModel(summary({ state: "deferred", candidateCount: 3, resultCount: 1 })).value,
    "Deferred",
  );
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
    prepassUsedSessionModel([
      summary({ boundaryId: initialBoundaryId, usedSessionModel: true }),
      summary({ boundaryId: initialBoundaryId, category: "skills" }),
    ]),
    true,
  );
  // Compaction boundaries never warn, even when a summary used the chat model.
  assert.equal(prepassUsedSessionModel([summary({ usedSessionModel: true })]), false);
  assert.equal(prepassUsedSessionModel([summary({ boundaryId: initialBoundaryId })]), false);
  assert.equal(prepassUsedSessionModel([]), false);
  assert.equal(prepassUsedSessionModel(null), false);
  assert.equal(prepassUsedSessionModel(undefined), false);
});
