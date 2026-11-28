import assert from "node:assert/strict";
import { test } from "node:test";
import type { ModelPickerOption } from "../../../providers/models/ModelPicker.tsx";
import { sortModelPickerOptions } from "../../../providers/models/ModelPickerOrder.ts";

const models: ModelPickerOption[] = [
  model("a", "Zulu", "Provider One", 3),
  model("b", "Alpha", "Provider Two", null),
  model("c", "Beta", "Provider One", 1),
];

void test("favorites stay first and retain the selected ordering", () => {
  const ordered = sortModelPickerOptions(models, new Set(["connection/b"]), {
    key: "model",
    descending: false,
  });
  assert.deepEqual(
    ordered.map(({ modelId }) => modelId),
    ["b", "c", "a"],
  );
});

void test("unknown prices stay last in either direction", () => {
  const ascending = sortModelPickerOptions(models, new Set(), {
    key: "input",
    descending: false,
  });
  const descending = sortModelPickerOptions(models, new Set(), {
    key: "input",
    descending: true,
  });
  assert.deepEqual(
    ascending.map(({ modelId }) => modelId),
    ["c", "a", "b"],
  );
  assert.deepEqual(
    descending.map(({ modelId }) => modelId),
    ["a", "c", "b"],
  );
});

function model(
  modelId: string,
  modelLabel: string,
  providerLabel: string,
  inputPrice: number | null,
): ModelPickerOption {
  return {
    key: `connection/${modelId}`,
    connectionId: "connection",
    modelId,
    modelLabel,
    providerLabel,
    inputPrice,
    outputPrice: inputPrice,
    priceSource: inputPrice === null ? null : "Test",
    priceCapturedAt: inputPrice === null ? null : "2026-11-28T00:00:00Z",
    supportsImages: false,
  };
}
