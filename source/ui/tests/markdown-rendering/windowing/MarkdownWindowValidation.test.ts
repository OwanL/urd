import test from "node:test";
import {
  createMarkdownPiece,
  createMarkdownValue,
  SyntheticMarkdownPageSource,
} from "../MarkdownRenderFixture.ts";
import {
  assertAppendRejected,
  linearSource,
  destinationParts,
  textSource,
  valueSource,
} from "./MarkdownWindowFixtures.ts";

void test("rejects malformed append pages without committing", async () => {
  const otherTable = new SyntheticMarkdownPageSource(
    [createMarkdownPiece(0, 8, "body")],
    [
      { kind: "document", children: [1, 0], pieces: [0, 1] },
      { kind: "table", parent: 0, children: [2, 0], nextSibling: 6 },
      { kind: "row", parent: 1, children: [3, 0], nextSibling: 4, header: true },
      { kind: "cell", parent: 2, header: true, headerCell: 3 },
      { kind: "row", parent: 1, children: [5, 0] },
      { kind: "cell", parent: 4, headerCell: 3 },
      { kind: "table", parent: 0, children: [7, 0], pieces: [0, 1] },
      { kind: "row", parent: 6, children: [8, 0], pieces: [0, 1] },
      { kind: "cell", parent: 7, pieces: [0, 1], headerCell: 3 },
    ],
  );
  const incompletePiece = new SyntheticMarkdownPageSource(
    [createMarkdownPiece(0, 1, "continued", { part: 1 })],
    [],
    { contextCount: 2 },
  );
  const cases: Array<{ source: SyntheticMarkdownPageSource; message?: RegExp }> = [
    {
      source: textSource([{ id: 1, pieces: [0, 1] }], { pieceGeneration: "stale-generation" }),
    },
    {
      source: valueSource([createMarkdownValue("destination", "/target")], {
        contextGeneration: "stale-generation",
      }),
    },
    {
      source: new SyntheticMarkdownPageSource([], [], {
        pieceCount: 1,
        transformPiecePage: (page) => ({ ...page, next: page.from, hasMore: true }),
      }),
    },
    {
      source: valueSource(destinationParts("/first/", "last"), {
        valuePageSize: 1,
        transformContextPage: (page) => ({ ...page, next: page.from, hasMore: true }),
      }),
    },
    {
      source: linearSource(
        [createMarkdownPiece(0, 1, "first"), createMarkdownPiece(2, 1, "skipped")],
        { transformPiecePage: (page) => ({ ...page, next: page.from + page.pieces.length }) },
      ),
    },
    { source: valueSource(destinationParts("/first/", "last", 2), { valuePageSize: 1 }) },
    { source: otherTable, message: /different table/u },
    { source: incompletePiece, message: /incomplete text piece/u },
    {
      source: valueSource(destinationParts("/first/", "last"), {
        valuePageSize: 1,
        transformContextPage: (page) =>
          page.from === 1 ? { ...page, context: { ...page.context, childCount: 1 } } : page,
      }),
      message: /context changed/u,
    },
  ];

  for (const { source, message } of cases) await assertAppendRejected(source, message);
});
