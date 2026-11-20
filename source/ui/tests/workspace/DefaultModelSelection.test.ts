import assert from "node:assert/strict";
import { test } from "node:test";
import { defaultModelSelection } from "../../workspace/DefaultModelSelection.ts";
import { selection } from "./WorkspaceCommandsFixture.ts";
import type { ModelOption } from "../../providers/models/ModelOptions.ts";

const first: ModelOption = {
  key: "first",
  modelLabel: "First",
  providerLabel: "Provider",
  selection: { ...selection, modelId: "first", reasoningEffort: "high" },
};
const second: ModelOption = {
  key: "second",
  modelLabel: "Second",
  providerLabel: "Provider",
  selection: { ...selection, modelId: "second", supportsImages: true, reasoningEffort: "high" },
};
const remembered = { ...selection, modelId: "second", reasoningEffort: "medium" };
const history = { ...selection, modelId: "first", reasoningEffort: "low" };

void test("prefers the workspace choice, then the last used model, using current catalog metadata", () => {
  assert.deepEqual(defaultModelSelection([first, second], remembered, history, false), {
    ...second.selection,
    reasoningEffort: "medium",
  });
  assert.deepEqual(defaultModelSelection([first], remembered, history, false), {
    ...first.selection,
    reasoningEffort: "low",
  });
  assert.deepEqual(defaultModelSelection([second], null, history, false), second.selection);
  assert.equal(defaultModelSelection([], null, null, false), null);
});

void test("does not choose a temporary first model from a partial catalog", () => {
  assert.equal(defaultModelSelection([second], null, null, true), null);
  assert.deepEqual(defaultModelSelection([first, second], null, null, false), first.selection);
});

void test("waits for an unmatched higher priority identity during discovery", () => {
  assert.equal(defaultModelSelection([first], remembered, history, true), null);
  assert.equal(defaultModelSelection([second], null, history, true), null);
  assert.deepEqual(defaultModelSelection([first, second], remembered, history, true), {
    ...second.selection,
    reasoningEffort: "medium",
  });
  const wrongProvider = {
    ...remembered,
    connection: { ...remembered.connection, id: "different" },
  };
  assert.equal(defaultModelSelection([second], wrongProvider, null, true), null);
  assert.deepEqual(defaultModelSelection([second], wrongProvider, null, false), second.selection);
});
