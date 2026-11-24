import assert from "node:assert/strict";
import test from "node:test";
import { viewerViewport } from "../../chat/browser/viewerViewport.ts";

void test("fits narrow and tall surfaces uniformly when the height cap limits desktop width", () => {
  assert.deepEqual(viewerViewport(360, 1200), { width: 1152, height: 3840 });
});

void test("fits wide and short surfaces uniformly within server limits", () => {
  assert.deepEqual(viewerViewport(6000, 1500), { width: 3840, height: 960 });
  assert.deepEqual(viewerViewport(1000, 100), { width: 2400, height: 240 });
  assert.deepEqual(viewerViewport(10_000, 100), { width: 3840, height: 240 });
  assert.deepEqual(viewerViewport(100, 10_000), { width: 240, height: 3840 });
});

void test("prefers desktop width, keeps native width when larger and rounds dimensions", () => {
  assert.deepEqual(viewerViewport(800, 600), { width: 1280, height: 960 });
  assert.deepEqual(viewerViewport(1600, 900), { width: 1600, height: 900 });
  assert.deepEqual(viewerViewport(801, 600), { width: 1280, height: 959 });
});

void test("caps large surfaces without changing their aspect ratio", () => {
  assert.deepEqual(viewerViewport(7680, 4320), { width: 3840, height: 2160 });
});

void test("returns null for invalid or unmeasured surfaces", () => {
  for (const [width, height] of [
    [0, 800],
    [-1, 800],
    [800, 0],
    [800, Number.NaN],
    [Number.POSITIVE_INFINITY, 800],
  ] as const)
    assert.equal(viewerViewport(width, height), null);
});
