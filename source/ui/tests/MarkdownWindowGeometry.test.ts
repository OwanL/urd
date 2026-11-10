import assert from "node:assert/strict";
import test from "node:test";
import {
  markdownChildGaps,
  MarkdownWindowGeometry,
  markdownTableColumns,
  requireMarkdownWindowUnits,
} from "../markdown-rendering/MarkdownWindowGeometry.ts";
import { MarkdownWindowAssembler } from "../markdown-rendering/MarkdownWindowAssembler.ts";
import {
  createMarkdownContext,
  createMarkdownPiece,
  SyntheticMarkdownPageSource,
} from "./MarkdownRenderFixture.ts";

void test("positional gaps partition omitted complete units, not source bytes", () => {
  for (const [count, positions, start, expected] of [
    [10, [], 0, "0:10"],
    [10, [2, 3, 7], 0, "0:2 4:7 8:10"],
    [10, [7, 2, 3, 2], 1, "1:2 4:7 8:10"],
    [3, [0, 1, 2], 0, ""],
  ] as const)
    assert.equal(
      markdownChildGaps(count, positions, start)
        .map(({ from, next }) => `${from}:${next}`)
        .join(" "),
      expected,
    );
});

void test("geometry is disposable and requires complete positional units", async () => {
  const source = new SyntheticMarkdownPageSource(
    [createMarkdownPiece(0, 2, "line\n")],
    [
      { kind: "document", children: [1, 1], pieces: [0, 1] },
      { kind: "code", parent: 0, children: [2, 4], pieces: [0, 1] },
      { kind: "codeLine", parent: 1, position: 2, pieces: [0, 1] },
    ],
  );
  const snapshot = await new MarkdownWindowAssembler(source, source.manifest).append(
    new AbortController().signal,
  );
  const parent = snapshot.contexts.get(1)!;
  const units = [snapshot.contexts.get(2)!];
  requireMarkdownWindowUnits(snapshot, parent, units);
  assert.throws(
    () => requireMarkdownWindowUnits({ ...snapshot, pieces: new Map() }, parent, units),
    /complete/u,
  );
  assert.throws(
    () => requireMarkdownWindowUnits(snapshot, parent, [...units, ...units]),
    /unique/u,
  );
  const geometry = new MarkdownWindowGeometry();
  assert.throws(() => geometry.configure("g", 0, "default"), /width/u);
  geometry.configure("g", 800, "default");
  assert.equal(geometry.gapHeight("code", 1, 2, 5), 72);
  const lengths = [4, 12];
  const cells = lengths.map((_, position) => ({
    context: createMarkdownContext(position, { position }),
    values: {},
  }));
  assert.deepEqual(
    markdownTableColumns(cells, new Map(lengths.map((length, position) => [position, length]))),
    [25, 75],
  );
});

void test("measurements replace estimates and fence generation, width and layout profile", () => {
  const geometry = new MarkdownWindowGeometry();
  let height = 10;
  const shell = { dataset: { context: "1", kind: "code" } };
  const line = {
    closest: () => shell,
    getBoundingClientRect: () => ({ top: 0, bottom: height, height }),
  };
  const root = {
    dataset: { markdownGeneration: "g" },
    getBoundingClientRect: () => ({ top: 0, bottom: 100, width: 800 }),
    querySelectorAll: (selector: string) => (selector.includes("codeLine") ? [line] : [shell]),
  } as unknown as HTMLElement;
  geometry.configure("g", 800, "a");
  assert.equal(geometry.measure(root), true);
  assert.equal(geometry.measure(root), false);
  assert.equal(geometry.gapHeight("code", 1, 0, 3), 30);
  height = 20;
  assert.equal(geometry.measure(root), true);
  assert.equal(geometry.gapHeight("code", 1, 0, 3), 60);
  for (const [generation, width, profile, accepted] of [
    ["other", 800, "a", false],
    ["g", 400, "a", false],
    ["g", 800, "b", true],
  ] as const) {
    geometry.configure(generation, width, profile);
    assert.equal(geometry.gapHeight("code", 1, 0, 3), 72);
    assert.equal(geometry.measure(root), accepted);
  }
});
