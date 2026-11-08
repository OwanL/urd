import assert from "node:assert/strict";
import test from "node:test";
import { placePopover } from "../components/PopoverGeometry.ts";

const viewport = { width: 800, height: 600 };
const size = { width: 200, height: 120 };

void test("keeps preferred placement and aligns to the anchor", () => {
  const anchor = { left: 100, top: 200, bottom: 230 };
  assert.deepEqual(placePopover(anchor, size, viewport), {
    placement: "below",
    left: 100,
    top: 236,
    maxWidth: 784,
    maxHeight: 356,
  });
  assert.equal(placePopover(anchor, size, viewport, "above").top, 74);
});

void test("flips only when the preferred side does not fit and the other side is larger", () => {
  const anchor = { left: 100, top: 530, bottom: 560 };
  assert.equal(placePopover(anchor, size, viewport).placement, "above");
  const tall = placePopover(anchor, { width: 200, height: 900 }, viewport);
  assert.equal(tall.placement, "above");
  assert.equal(tall.maxHeight, 516);
  assert.equal(tall.top, 8);
});

void test("clamps horizontal overflow and oversized content", () => {
  assert.equal(placePopover({ left: 790, top: 30, bottom: 60 }, size, viewport).left, 592);
  assert.equal(placePopover({ left: -30, top: 30, bottom: 60 }, size, viewport).left, 8);
  const wide = placePopover(
    { left: 30, top: 30, bottom: 60 },
    { width: 900, height: 20 },
    viewport,
  );
  assert.equal(wide.left, 8);
  assert.equal(wide.maxWidth, 784);
});

void test("handles offscreen anchors and tiny viewports without negative available sizes", () => {
  const above = placePopover({ left: 900, top: 900, bottom: 930 }, size, viewport);
  assert.equal(above.placement, "above");
  assert.equal(above.top, 472);
  assert.equal(above.maxHeight, 584);
  const tiny = placePopover({ left: 0, top: 0, bottom: 20 }, size, { width: 10, height: 10 });
  assert.equal(tiny.maxWidth, 0);
  assert.equal(tiny.maxHeight, 0);
  assert.equal(tiny.left, 5);
  assert.equal(tiny.top, 5);
});
