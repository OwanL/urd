import assert from "node:assert/strict";
import test from "node:test";
import { moveTab, tabDragIndex, tabDragOffset } from "../app/TabOrder.ts";

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

const positions = Object.freeze([
  Object.freeze({ id: "a", left: 40, right: 240 }),
  Object.freeze({ id: "b", left: 245, right: 345 }),
  Object.freeze({ id: "c", left: 350, right: 500 }),
]);

void test("facing edges cross early, independently of the grab offset and unequal widths", () => {
  // Grab near the leading edge: the pointer is far short of b's center (295).
  assert.equal(tabDragIndex(positions, 0, 0, 105, 10), 0);
  assert.equal(tabDragIndex(positions, 0, 0, 106, 10), 1);
  // The same left edge with a grab near the opposite end gives the same slot.
  assert.equal(tabDragIndex(positions, 0, 0, 286, 190), 1);
  // Moving left uses the leading edge even with the pointer still right of b.
  assert.equal(tabDragIndex(positions, 2, 2, 435, 140), 2);
  assert.equal(tabDragIndex(positions, 2, 2, 434, 140), 1);
  assert.equal(tabDragIndex(positions, 2, 2, 304, 10), 1);
});

void test("shifted slots do not jitter at a crossing and backtracking restores the order", () => {
  let to = tabDragIndex(positions, 0, 0, 96, 0);
  assert.equal(to, 1);
  for (const left of [96, 96, 95, 94, 91, 90]) {
    to = tabDragIndex(positions, 0, to, left, 0);
    assert.equal(to, 1);
  }
  to = tabDragIndex(positions, 0, to, 89, 0);
  assert.equal(to, 0);
  to = tabDragIndex(positions, 0, to, 226, 0);
  assert.equal(to, 2);
  for (const left of [226, 225, 221, 220]) {
    to = tabDragIndex(positions, 0, to, left, 0);
    assert.equal(to, 2);
  }
  to = tabDragIndex(positions, 0, to, 219, 0);
  assert.equal(to, 1);
  assert.equal(tabDragIndex(positions, 0, to, 40, 0), 0);
  assert.equal(tabDragIndex(positions, 0, 0, 1000, 0), 2);
  assert.equal(tabDragIndex(positions, 2, 2, -1000, 0), 0);
});

void test("sibling transforms reserve the dragged width and gap without changing geometry", () => {
  assert.deepEqual(
    positions.map((_, index) => tabDragOffset(positions, 0, 2, index)),
    [0, -205, -205],
  );
  assert.deepEqual(
    positions.map((_, index) => tabDragOffset(positions, 2, 0, index)),
    [155, 155, 0],
  );
  assert.deepEqual(
    positions.map((_, index) => tabDragOffset(positions, 0, 0, index)),
    [0, 0, 0],
  );
  const single = [positions[0]!];
  assert.equal(tabDragIndex(single, 0, 0, 900, 20), 0);
  // Translating the snapshot and pointer equally (viewport offset/scroll) changes nothing.
  const shifted = positions.map((tab) => ({
    ...tab,
    left: tab.left - 300,
    right: tab.right - 300,
  }));
  assert.equal(tabDragIndex(shifted, 0, 0, 106 - 300, 10), 1);
});

void test("single-step keyboard targets move once, without wrapping at boundaries", () => {
  assert.deepEqual(ids(moveTab(tabs, "b", "a")), ["b", "a", "c"]);
  assert.deepEqual(ids(moveTab(tabs, "b", null)), ["a", "c", "b"]);
});
