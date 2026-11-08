import assert from "node:assert/strict";
import test from "node:test";
import { moveTab, tabInsertion } from "../app/TabOrder.ts";

const tabs = Object.freeze([
  Object.freeze({ id: "a", title: "First", draft: "Keep me" }),
  Object.freeze({ id: "b", title: "Second", draft: "Still here" }),
  Object.freeze({ id: "c", title: "Third", draft: "Unchanged" }),
]);
const ids = (items: typeof tabs) => items.map((tab) => tab.id);

void test("moves before a target or to the end without replacing tab data", () => {
  const left = moveTab(tabs, "c", "a");
  assert.deepEqual(ids(left), ["c", "a", "b"]);
  assert.equal(left[0], tabs[2]);
  assert.equal(left[1], tabs[0]);
  assert.equal(left[2], tabs[1]);
  assert.deepEqual(ids(moveTab(tabs, "a", "c")), ["b", "a", "c"]);
  assert.deepEqual(ids(moveTab(tabs, "a", null)), ["b", "c", "a"]);
  assert.deepEqual(ids(tabs), ["a", "b", "c"]);
});

void test("unchanged, missing and self-target moves preserve the original order", () => {
  for (const [id, beforeId] of [
    ["a", "b"],
    ["b", "c"],
    ["c", null],
    ["a", "a"],
    ["missing", "a"],
    ["a", "missing"],
  ] as const)
    assert.equal(moveTab(tabs, id, beforeId), tabs);
  assert.equal(moveTab([], "a", null).length, 0);
  const single = [tabs[0]!];
  assert.equal(moveTab(single, "a", null), single);
});

void test("insertion uses remaining tab midpoints, including gaps and viewport offsets", () => {
  const positions = [
    { id: "a", left: -80, right: 20 },
    { id: "c", left: 140, right: 300 },
  ];
  assert.equal(tabInsertion(positions, -31), "a");
  assert.equal(tabInsertion(positions, -30), "c");
  assert.equal(tabInsertion(positions, 60), "c");
  assert.equal(tabInsertion(positions, 219), "c");
  assert.equal(tabInsertion(positions, 220), null);
  assert.equal(tabInsertion(positions, 500), null);
  assert.equal(tabInsertion([], 100), null);
});

void test("single-step keyboard targets move once, without wrapping at boundaries", () => {
  assert.deepEqual(ids(moveTab(tabs, "b", "a")), ["b", "a", "c"]);
  assert.deepEqual(ids(moveTab(tabs, "b", null)), ["a", "c", "b"]);
});
