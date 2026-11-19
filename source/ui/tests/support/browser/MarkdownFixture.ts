import {
  createMarkdownContext,
  createMarkdownPiece,
  createMarkdownValue,
} from "../../markdown-rendering/MarkdownRenderFixture.ts";
import type {
  MarkdownRenderContext,
  MarkdownTextPiece,
  MarkdownValueFragment,
} from "../../../markdown-rendering/MarkdownRenderModel.ts";

export const imageKey = "a".repeat(64);
const elementKinds = [
  "document",
  "paragraph",
  "heading",
  "quote",
  "list",
  "item",
  "code",
  "codeLine",
  "table",
  "row",
  "cell",
  "emphasis",
  "strong",
  "inlineCode",
  "link",
  "thematicBreak",
  "image",
] as const;
const textKinds = ["text", "literal", "code", "softBreak", "hardBreak", "thematicBreak"] as const;
const mappings = ["exact", "containing", "container"] as const;
const valueKinds = [
  "destination",
  "title",
  "language",
  "fenceInfo",
  "orderedStart",
  "imageKey",
] as const;

export function markdownFixture(kind: "plain" | "markdown" | "image", plainText = "") {
  const contexts: MarkdownRenderContext[] = [];
  const pieces: MarkdownTextPiece[] = [];
  const values = new Map<number, MarkdownValueFragment[]>();
  let text = "";
  contexts.push(createMarkdownContext(0, { kind: "document" }));
  const top: number[] = [];
  function block(
    label: string,
    type: "paragraph" | "heading" | "image" = "paragraph",
    sourceText = label,
    labelOffset = 0,
  ) {
    const start = Buffer.byteLength(text);
    const labelStart = start + Buffer.byteLength(sourceText.slice(0, labelOffset));
    text += sourceText + "\n\n";
    const id = contexts.length;
    top.push(id);
    contexts.push(
      createMarkdownContext(id, {
        kind: type,
        parent: 0,
        firstPiece: pieces.length,
        pieceCount: 1,
        source: { start, end: start + Buffer.byteLength(sourceText) },
        level: type === "heading" ? 1 : 0,
        valueCount: type === "image" ? 1 : 0,
      }),
    );
    pieces.push(
      createMarkdownPiece(pieces.length, id, label, {
        source: { start: labelStart, end: labelStart + Buffer.byteLength(label) },
      }),
    );
    if (type === "image") values.set(id, [createMarkdownValue("imageKey", imageKey)]);
  }
  function table() {
    const rows = [
      { header: true, cells: ["Feature", "Example", "Purpose"] },
      { header: false, cells: ["Heading", "Long Markdown fixture", "Document title"] },
      { header: false, cells: ["Paragraph", "Markdown row 900", "Windowing target"] },
      { header: false, cells: ["Code", "Code line 12", "Fenced block"] },
    ] as const;
    const alignments = ["left", "center", "right"] as const;
    const start = Buffer.byteLength(text);
    const id = contexts.length;
    const firstPiece = pieces.length;
    const rowIds: number[] = [];
    const headerCells: number[] = [];
    top.push(id);
    contexts.push(
      createMarkdownContext(id, {
        kind: "table",
        parent: 0,
        firstChild: id + 1,
        childCount: rows.length,
        firstPiece,
        pieceCount: rows.length * rows[0].cells.length,
        source: { start, end: start },
      }),
    );
    rows.forEach((row, position) => {
      const rowStart = Buffer.byteLength(text);
      const line = `| ${row.cells.join(" | ")} |`;
      const rowId = contexts.length;
      const rowFirstPiece = pieces.length;
      const cellIds: number[] = [];
      rowIds.push(rowId);
      text += line + "\n";
      contexts.push(
        createMarkdownContext(rowId, {
          kind: "row",
          parent: id,
          firstChild: rowId + 1,
          childCount: row.cells.length,
          firstPiece: rowFirstPiece,
          pieceCount: row.cells.length,
          source: { start: rowStart, end: rowStart + Buffer.byteLength(line) },
          position,
          header: row.header,
        }),
      );
      let cellStart = rowStart + 2;
      row.cells.forEach((label, column) => {
        const cellId = contexts.length;
        cellIds.push(cellId);
        const pieceId = pieces.length;
        const cellEnd = cellStart + Buffer.byteLength(label);
        contexts.push(
          createMarkdownContext(cellId, {
            kind: "cell",
            parent: rowId,
            position: column,
            nextSibling: column + 1 < row.cells.length ? cellId + 1 : -1,
            firstPiece: pieceId,
            pieceCount: 1,
            source: { start: cellStart, end: cellEnd },
            header: row.header,
            headerCell: row.header ? cellId : headerCells[column]!,
            alignment: alignments[column]!,
          }),
        );
        pieces.push(
          createMarkdownPiece(pieceId, cellId, label, {
            source: { start: cellStart, end: cellEnd },
          }),
        );
        if (row.header) headerCells.push(cellId);
        cellStart = cellEnd + 3;
      });
      if (row.header) text += "| :--- | :---: | ---: |\n";
      contexts[rowId] = { ...contexts[rowId]!, firstChild: cellIds[0]! };
    });
    rowIds.forEach((rowId, position) => {
      contexts[rowId] = { ...contexts[rowId]!, nextSibling: rowIds[position + 1] ?? -1 };
    });
    contexts[id] = { ...contexts[id]!, source: { start, end: Buffer.byteLength(text) } };
  }
  if (kind === "markdown") {
    block("Long Markdown fixture", "heading", "# Long Markdown fixture", 2);
    for (let i = 1; i <= 900; i++) block(`Markdown row ${String(i).padStart(3, "0")}`);
    // A fenced code block with individually addressable code lines.
    const start = Buffer.byteLength(text);
    text += "```text\n";
    const codeId = contexts.length;
    const firstPiece = pieces.length;
    top.push(codeId);
    contexts.push(createMarkdownContext(codeId, { kind: "code", parent: 0, valueCount: 1 }));
    values.set(codeId, [createMarkdownValue("language", "text")]);
    const children: number[] = [];
    for (let i = 1; i <= 12; i++) {
      const label = `Code line ${String(i).padStart(2, "0")}`;
      const lineStart = Buffer.byteLength(text);
      text += label + "\n";
      const id = contexts.length;
      children.push(id);
      contexts.push(
        createMarkdownContext(id, {
          kind: "codeLine",
          parent: codeId,
          position: i - 1,
          firstPiece: pieces.length,
          pieceCount: 1,
          source: { start: lineStart, end: Buffer.byteLength(text) },
        }),
      );
      pieces.push(
        createMarkdownPiece(pieces.length, id, label + "\n", {
          kind: "code",
          source: { start: lineStart, end: Buffer.byteLength(text) },
        }),
      );
    }
    text += "```\n";
    const end = Buffer.byteLength(text);
    text += "\n";
    children.forEach((id, index) => {
      contexts[id] = { ...contexts[id]!, nextSibling: children[index + 1] ?? -1 };
    });
    contexts[codeId] = {
      ...contexts[codeId]!,
      firstChild: children[0]!,
      childCount: children.length,
      firstPiece,
      pieceCount: children.length,
      source: { start, end },
    };
    table();
  } else if (kind === "image") {
    block(
      "Delayed managed image",
      "image",
      "![Delayed managed image](https://example.invalid/delayed-managed-image.png)",
      2,
    );
    for (let i = 1; i <= 80; i++) block(`Image paragraph ${String(i).padStart(3, "0")}`);
  } else {
    block(plainText);
    text = plainText;
    contexts[1] = { ...contexts[1]!, source: { start: 0, end: Buffer.byteLength(text) } };
  }
  top.forEach((id, index) => {
    contexts[id] = { ...contexts[id]!, nextSibling: top[index + 1] ?? -1 };
  });
  contexts[0] = {
    ...contexts[0]!,
    firstChild: top[0]!,
    childCount: top.length,
    firstPiece: 0,
    pieceCount: pieces.length,
    source: { start: 0, end: Buffer.byteLength(text) },
  };
  const wireContext = (context: MarkdownRenderContext) => ({
    ...context,
    kind: elementKinds.indexOf(context.kind),
    alignment: ["none", "left", "center", "right"].indexOf(context.alignment),
  });
  const wirePiece = (piece: MarkdownTextPiece) => ({
    ...piece,
    kind: textKinds.indexOf(piece.kind),
    mapping: mappings.indexOf(piece.mapping),
  });
  const wireValues = (id: number) =>
    (values.get(id) ?? []).map((value) => ({ ...value, kind: valueKinds.indexOf(value.kind) }));
  return {
    text: kind === "plain" ? plainText : text,
    contexts,
    pieces,
    wireContext,
    wirePiece,
    wireValues,
  };
}
