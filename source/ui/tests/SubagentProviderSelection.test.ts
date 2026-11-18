import assert from "node:assert/strict";
import test from "node:test";
import { toggleSubagentProvider } from "../providers/SubagentProviderSelection.ts";

void test("disabling one connection from the open policy locks to the other loaded IDs", () => {
  assert.deepEqual(toggleSubagentProvider(null, ["a", "b"], "a", false), ["b"]);
  assert.deepEqual(toggleSubagentProvider(null, ["a"], "a", false), []);
});

void test("individual toggles preserve the explicit mode even when all loaded IDs are enabled", () => {
  assert.deepEqual(toggleSubagentProvider(["a"], ["a", "b"], "b", true), ["a", "b"]);
  assert.deepEqual(toggleSubagentProvider([], ["a", "b"], "b", true), ["b"]);
});

void test("a selection can remove an unavailable saved connection without adding new ones", () => {
  assert.deepEqual(toggleSubagentProvider(["a", "gone"], ["a", "b"], "gone", false), ["a"]);
});
