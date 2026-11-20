import assert from "node:assert/strict";
import { MarkdownWindowAssembler } from "../../../markdown-rendering/windowing/MarkdownWindowAssembler.ts";
import {
  createMarkdownPiece,
  createMarkdownValue,
  SyntheticMarkdownPageSource,
  type MarkdownContextDescriptor,
  type MarkdownRenderFixtureOptions,
} from "../MarkdownRenderFixture.ts";

export function signal() {
  return new AbortController().signal;
}

export function assertWindow(
  snapshot: MarkdownWindowAssembler["snapshot"],
  expected: { from?: number; next?: number; hasMore?: boolean },
) {
  if (expected.from !== undefined) assert.equal(snapshot.from, expected.from);
  if (expected.next !== undefined) assert.equal(snapshot.next, expected.next);
  if (expected.hasMore !== undefined) assert.equal(snapshot.hasMore, expected.hasMore);
}

export function assertIds(map: ReadonlyMap<number, unknown>, ids: number[]) {
  assert.deepEqual(
    [...map.keys()].sort((left, right) => left - right),
    ids,
  );
}

export async function assertAppendRejected(
  source: SyntheticMarkdownPageSource,
  message: RegExp = /Markdown/u,
): Promise<void> {
  const assembler = new MarkdownWindowAssembler(source, source.manifest);
  await assert.rejects(assembler.append(signal()), message);
  assertWindow(assembler.snapshot, { from: 0, next: 0 });
  assert.equal(assembler.snapshot.pieces.size, 0);
  assert.equal(assembler.snapshot.contexts.size, 0);
}

type TrimRequest = Parameters<MarkdownWindowAssembler["trim"]>[0];

export function trim(
  assembler: MarkdownWindowAssembler,
  active: TrimRequest["active"],
  retain?: TrimRequest["retain"],
  abort = signal(),
) {
  return assembler.trim(
    {
      generation: assembler.snapshot.generation,
      active,
      ...(retain ? { retain } : {}),
    },
    abort,
  );
}

export function linearSource(
  pieces: ReturnType<typeof createMarkdownPiece>[],
  options: MarkdownRenderFixtureOptions = {},
) {
  const pieceCount =
    options.pieceCount ?? pieces.reduce((count, piece) => Math.max(count, piece.id + 1), 0);
  return new SyntheticMarkdownPageSource(
    pieces,
    [
      { kind: "document", children: [1, 1], pieces: [0, pieceCount] },
      { parent: 0, pieces: [0, pieceCount] },
    ],
    options,
  );
}

export function destinationParts(first: string, last: string, lastPart = 1) {
  return [
    createMarkdownValue("destination", first, 0, true),
    createMarkdownValue("destination", last, lastPart),
  ];
}

export function textSource(
  contexts: readonly MarkdownContextDescriptor[],
  options: MarkdownRenderFixtureOptions = {},
) {
  return new SyntheticMarkdownPageSource([createMarkdownPiece(0, 1, "text")], contexts, options);
}

export function valueSource(
  values: ReturnType<typeof createMarkdownValue>[],
  options: MarkdownRenderFixtureOptions = {},
) {
  return textSource(
    [
      { kind: "document", children: [1, 1], pieces: [0, 1] },
      { parent: 0, pieces: [0, 1], valueCount: values.length, values },
    ],
    options,
  );
}

export function tableSource(options: MarkdownRenderFixtureOptions = {}) {
  return new SyntheticMarkdownPageSource(
    [
      createMarkdownPiece(0, 3, "header"),
      createMarkdownPiece(1, 3, ""),
      ...Array.from({ length: 4 }, (_, offset) =>
        createMarkdownPiece(offset + 2, 6, `body-${offset}`),
      ),
      createMarkdownPiece(6, 7, "outside"),
    ],
    [
      { kind: "document", children: [1, 2], pieces: [0, 7] },
      { kind: "table", parent: 0, children: [2, 2], nextSibling: 7, pieces: [0, 6] },
      { kind: "row", parent: 1, children: [3, 2], nextSibling: 5, pieces: [0, 2], header: true },
      {
        kind: "cell",
        parent: 2,
        nextSibling: 4,
        pieces: [0, 2],
        valueCount: 2,
        header: true,
        headerCell: 3,
        values: [
          createMarkdownValue("title", "column ", 0, true),
          createMarkdownValue("title", "name", 1),
        ],
      },
      { kind: "cell", parent: 2, pieces: [2, 0], header: true, headerCell: 4 },
      { kind: "row", parent: 1, children: [6, 1], pieces: [2, 4] },
      { kind: "cell", parent: 5, pieces: [2, 4], headerCell: 3 },
      { parent: 0, pieces: [6, 1] },
    ],
    options,
  );
}
