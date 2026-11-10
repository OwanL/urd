import assert from "node:assert/strict";
import test from "node:test";
import { DisplayAssembler } from "../display/DisplayAssembler.ts";
import { safeDisplayLink } from "../display/DisplayLink.ts";
import {
  createDisplayContext,
  createDisplayPiece,
  createDisplayValue,
  SyntheticDisplaySource,
} from "./DisplayFixture.ts";

function signal() {
  return new AbortController().signal;
}

function assertUncommitted(assembler: DisplayAssembler, from: number) {
  const snapshot = assembler.snapshot;
  assert.equal(snapshot.from, from);
  assert.equal(snapshot.next, from);
  assert.equal(snapshot.pieces.size, 0);
  assert.equal(snapshot.contexts.size, 0);
}

void test("assembles continued values and pieces across appended pages", async () => {
  const source = new SyntheticDisplaySource(
    [
      createDisplayPiece(0, 1, "first ", { part: 0, continued: true }),
      createDisplayPiece(1, 1, "second", { part: 1 }),
    ],
    [
      {
        context: createDisplayContext(0, {
          kind: "document",
          firstChild: 1,
          firstPiece: 0,
          pieceCount: 2,
        }),
      },
      {
        context: createDisplayContext(1, {
          parent: 0,
          firstPiece: 0,
          pieceCount: 2,
          valueCount: 2,
        }),
        values: [
          createDisplayValue("destination", "/example/", 0, true),
          createDisplayValue("destination", "target", 1),
        ],
      },
    ],
    { piecePageSize: 1, valuePageSize: 1 },
  );
  const assembler = new DisplayAssembler(source, "fixture-generation");

  const first = await assembler.append(signal());
  assert.equal(first.next, 1);
  assert.equal(first.hasMore, true);
  assert.deepEqual([...first.pieces.keys()], [0]);

  const second = await assembler.append(signal());
  assert.equal(second.next, 2);
  assert.equal(second.hasMore, false);
  assert.deepEqual([...second.pieces.keys()], [0, 1]);
  assert.equal(second.contexts.get(1)?.values.destination, "/example/target");
  assert.ok(
    source.contextRequests.some((request) => request.id === 1 && request.cursor.index === 1),
  );
  assert.ok(source.pieceRequests.some((request) => request.index === 1));
});

void test("accepts a middle-window initial part and continues it on the next append", async () => {
  const source = new SyntheticDisplaySource(
    [
      createDisplayPiece(3, 1, "middle ", { part: 4, continued: true }),
      createDisplayPiece(4, 1, "end", { part: 5 }),
    ],
    [
      {
        context: createDisplayContext(0, {
          kind: "document",
          firstChild: 1,
          firstPiece: 3,
          pieceCount: 2,
        }),
      },
      {
        context: createDisplayContext(1, { parent: 0, firstPiece: 3, pieceCount: 2 }),
      },
    ],
    { piecePageSize: 1 },
  );
  const assembler = new DisplayAssembler(source, "fixture-generation", 3);

  const middle = await assembler.append(signal());
  assert.equal(middle.from, 3);
  assert.equal(middle.next, 4);
  assert.deepEqual([...middle.pieces.keys()], [3]);

  const end = await assembler.append(signal());
  assert.equal(end.next, 5);
  assert.equal(end.hasMore, false);
  assert.deepEqual([...end.pieces.keys()], [3, 4]);

  const invalid = new SyntheticDisplaySource(
    [createDisplayPiece(0, 1, "continued", { part: 1 })],
    [],
  );
  await assert.rejects(
    new DisplayAssembler(invalid, "fixture-generation").append(signal()),
    /incomplete piece/u,
  );
});

void test("hydrates middle ancestry and a detached table header cell and its pieces", async () => {
  const contexts = [
    {
      context: createDisplayContext(0, {
        kind: "document",
        firstChild: 1,
        firstPiece: 2,
        pieceCount: 9,
      }),
    },
    {
      context: createDisplayContext(1, {
        kind: "table",
        parent: 0,
        firstChild: 2,
        firstPiece: 2,
        pieceCount: 9,
      }),
    },
    {
      context: createDisplayContext(2, {
        kind: "row",
        parent: 1,
        firstChild: 3,
        nextSibling: 4,
        firstPiece: 2,
        pieceCount: 8,
        header: true,
      }),
    },
    {
      context: createDisplayContext(3, {
        kind: "cell",
        parent: 2,
        firstPiece: 2,
        pieceCount: 8,
        header: true,
        headerCell: 3,
      }),
    },
    {
      context: createDisplayContext(4, {
        kind: "row",
        parent: 1,
        firstChild: 5,
        firstPiece: 10,
        pieceCount: 1,
      }),
    },
    {
      context: createDisplayContext(5, {
        kind: "cell",
        parent: 4,
        firstPiece: 10,
        pieceCount: 1,
        headerCell: 3,
      }),
    },
  ];
  const headerPieces = Array.from({ length: 8 }, (_, offset) =>
    createDisplayPiece(2 + offset, 3, offset === 0 ? "heading" : " header"),
  );
  const source = new SyntheticDisplaySource(
    [...headerPieces, createDisplayPiece(10, 5, "value")],
    contexts,
  );
  const assembler = new DisplayAssembler(source, "fixture-generation", 10);

  const snapshot = await assembler.append(signal());

  for (const id of [0, 1, 2, 3, 4, 5]) assert.ok(snapshot.contexts.has(id), `context ${id}`);
  assert.ok(snapshot.pieces.has(2), "detached header piece");
  assert.ok(snapshot.pieces.has(10), "window piece");
});

void test("rejects a piece page from another generation without committing", async () => {
  const source = new SyntheticDisplaySource(
    [createDisplayPiece(0, 1, "text")],
    [{ context: createDisplayContext(1, { firstPiece: 0, pieceCount: 1 }) }],
    { pieceGeneration: "stale-generation" },
  );
  const assembler = new DisplayAssembler(source, "fixture-generation");

  await assert.rejects(assembler.append(signal()));
  assertUncommitted(assembler, 0);
});

void test("rejects a context page from another generation without committing", async () => {
  const source = new SyntheticDisplaySource(
    [createDisplayPiece(0, 1, "text")],
    [
      {
        context: createDisplayContext(1, { firstPiece: 0, pieceCount: 1, valueCount: 1 }),
        values: [createDisplayValue("destination", "/target")],
      },
    ],
    { contextGeneration: "stale-generation" },
  );
  const assembler = new DisplayAssembler(source, "fixture-generation");

  await assert.rejects(assembler.append(signal()));
  assertUncommitted(assembler, 0);
});

void test("rejects a nonadvancing piece page without committing", async () => {
  const source = new SyntheticDisplaySource([], [], {
    transformPiecePage: (page) => ({ ...page, next: page.from, hasMore: true }),
  });
  const assembler = new DisplayAssembler(source, "fixture-generation");

  await assert.rejects(assembler.append(signal()));
  assertUncommitted(assembler, 0);
});

void test("rejects a nonadvancing context page without committing", async () => {
  const source = new SyntheticDisplaySource(
    [createDisplayPiece(0, 1, "text")],
    [
      {
        context: createDisplayContext(1, { firstPiece: 0, pieceCount: 1, valueCount: 2 }),
        values: [
          createDisplayValue("destination", "/first/", 0, true),
          createDisplayValue("destination", "last", 1),
        ],
      },
    ],
    {
      valuePageSize: 1,
      transformContextPage: (page) => ({ ...page, next: page.from, hasMore: true }),
    },
  );
  const assembler = new DisplayAssembler(source, "fixture-generation");

  await assert.rejects(assembler.append(signal()));
  assertUncommitted(assembler, 0);
});

void test("rejects skipped piece IDs without committing", async () => {
  const source = new SyntheticDisplaySource(
    [createDisplayPiece(0, 1, "first"), createDisplayPiece(2, 1, "skipped")],
    [
      {
        context: createDisplayContext(0, {
          kind: "document",
          firstChild: 1,
          firstPiece: 0,
          pieceCount: 3,
        }),
      },
      { context: createDisplayContext(1, { parent: 0, firstPiece: 0, pieceCount: 3 }) },
    ],
    { transformPiecePage: (page) => ({ ...page, next: page.from + page.pieces.length }) },
  );
  const assembler = new DisplayAssembler(source, "fixture-generation");

  await assert.rejects(assembler.append(signal()));
  assertUncommitted(assembler, 0);
});

void test("rejects discontinuous context value parts without committing", async () => {
  const source = new SyntheticDisplaySource(
    [createDisplayPiece(0, 1, "text")],
    [
      {
        context: createDisplayContext(1, { firstPiece: 0, pieceCount: 1, valueCount: 2 }),
        values: [
          createDisplayValue("destination", "/first/", 0, true),
          createDisplayValue("destination", "last", 2),
        ],
      },
    ],
    { valuePageSize: 1 },
  );
  const assembler = new DisplayAssembler(source, "fixture-generation");

  await assert.rejects(assembler.append(signal()));
  assertUncommitted(assembler, 0);
});

void test("rejects a header reference from another table", async () => {
  const contexts = [
    {
      context: createDisplayContext(0, {
        kind: "document",
        firstChild: 1,
        firstPiece: 0,
        pieceCount: 1,
      }),
    },
    {
      context: createDisplayContext(1, {
        kind: "table",
        parent: 0,
        firstChild: 2,
        nextSibling: 6,
      }),
    },
    {
      context: createDisplayContext(2, {
        kind: "row",
        parent: 1,
        firstChild: 3,
        nextSibling: 4,
        header: true,
      }),
    },
    { context: createDisplayContext(3, { kind: "cell", parent: 2, header: true, headerCell: 3 }) },
    { context: createDisplayContext(4, { kind: "row", parent: 1, firstChild: 5 }) },
    { context: createDisplayContext(5, { kind: "cell", parent: 4, headerCell: 3 }) },
    {
      context: createDisplayContext(6, {
        kind: "table",
        parent: 0,
        firstChild: 7,
        firstPiece: 0,
        pieceCount: 1,
      }),
    },
    {
      context: createDisplayContext(7, {
        kind: "row",
        parent: 6,
        firstChild: 8,
        firstPiece: 0,
        pieceCount: 1,
      }),
    },
    {
      context: createDisplayContext(8, {
        kind: "cell",
        parent: 7,
        firstPiece: 0,
        pieceCount: 1,
        headerCell: 3,
      }),
    },
  ];
  const source = new SyntheticDisplaySource([createDisplayPiece(0, 8, "body")], contexts);
  const assembler = new DisplayAssembler(source, "fixture-generation");

  await assert.rejects(assembler.append(signal()), /different table/u);
  assertUncommitted(assembler, 0);
});

void test("keeps UTF-8 source ranges opaque and detaches page records", async () => {
  const text = "A😀";
  const contextRange = { start: 0, end: new TextEncoder().encode(text).length };
  const pieceRange = { start: 1, end: new TextEncoder().encode(text).length };
  const root = createDisplayContext(0, {
    kind: "document",
    firstChild: 1,
    firstPiece: 0,
    pieceCount: 1,
    source: contextRange,
  });
  const paragraph = createDisplayContext(1, {
    parent: 0,
    firstPiece: 0,
    pieceCount: 1,
    source: contextRange,
  });
  const piece = createDisplayPiece(0, 1, "😀", { source: pieceRange });
  const source = new SyntheticDisplaySource([piece], [{ context: root }, { context: paragraph }]);
  const assembler = new DisplayAssembler(source, "fixture-generation");

  const snapshot = await assembler.append(signal());
  contextRange.end = 2;
  pieceRange.start = 0;

  assert.equal(text.length, 3);
  assert.equal(snapshot.contexts.get(1)?.context.source.end, 5);
  assert.deepEqual(snapshot.pieces.get(0)?.source, { start: 1, end: 5 });
});

void test("rejects a context descriptor that changes between value pages", async () => {
  const source = new SyntheticDisplaySource(
    [createDisplayPiece(0, 1, "text")],
    [
      {
        context: createDisplayContext(0, {
          kind: "document",
          firstChild: 1,
          firstPiece: 0,
          pieceCount: 1,
        }),
      },
      {
        context: createDisplayContext(1, {
          parent: 0,
          firstPiece: 0,
          pieceCount: 1,
          valueCount: 2,
        }),
        values: [
          createDisplayValue("destination", "/first/", 0, true),
          createDisplayValue("destination", "last", 1),
        ],
      },
    ],
    {
      valuePageSize: 1,
      transformContextPage: (page) =>
        page.from === 1 ? { ...page, context: { ...page.context, position: 1 } } : page,
    },
  );
  const assembler = new DisplayAssembler(source, "fixture-generation");

  await assert.rejects(assembler.append(signal()), /context changed/u);
  assertUncommitted(assembler, 0);
});

void test("allows only safe absolute and relative display links", () => {
  for (const destination of [
    "https://example.test/path",
    "HTTP://example.test/path",
    "mailto:user@example.test",
    "relative/path?query=1#section",
    "/root/path",
    "?query=1",
    "#section",
  ])
    assert.equal(safeDisplayLink(destination), destination);

  for (const destination of [
    undefined,
    "javascript:alert(1)",
    "data:text/html,unsafe",
    "ftp://example.test/file",
    "//example.test/path",
    "https:\\\\example.test/path",
    "https://example.test/line\nbreak",
    "https://example.test/\u0000",
    "https://example.test/\u0085",
  ])
    assert.equal(safeDisplayLink(destination), undefined);
});

void test("accepts an empty terminal piece page", async () => {
  const source = new SyntheticDisplaySource([], []);
  const assembler = new DisplayAssembler(source, "fixture-generation", 7);

  const snapshot = await assembler.append(signal());

  assert.equal(snapshot.from, 7);
  assert.equal(snapshot.next, 7);
  assert.equal(snapshot.hasMore, false);
  assert.equal(snapshot.pieces.size, 0);
});
