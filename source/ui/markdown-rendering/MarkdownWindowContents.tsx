import type { ComponentChildren, VNode } from "preact";
import type {
  AssembledMarkdownContext,
  MarkdownRenderSnapshot,
  MarkdownSourceRange,
  MarkdownTextPiece,
} from "./MarkdownRenderModel.ts";
import {
  markdownChildGaps,
  markdownRootGaps,
  requireMarkdownWindowUnits,
  type MarkdownWindowGeometry,
} from "./windowing/MarkdownWindowGeometry.ts";

type Entry = { at: number; order: number; node: VNode };

const ordinaryGapParents: ReadonlySet<string> = new Set([
  "paragraph",
  "heading",
  "quote",
  "list",
  "item",
  "emphasis",
  "strong",
  "inlineCode",
  "link",
]);

function rangeAttributes(source: MarkdownSourceRange) {
  return { "data-source-start": source.start, "data-source-end": source.end };
}

function pieceAttributes(piece: MarkdownTextPiece) {
  return {
    ...rangeAttributes(piece.source),
    "data-piece": piece.id,
    "data-part": piece.part,
    "data-continued": piece.continued,
    "data-mapping": piece.mapping,
    "data-text-kind": piece.kind,
  };
}

function pieceNode(piece: MarkdownTextPiece) {
  const attributes = pieceAttributes(piece);
  if (piece.kind === "thematicBreak") return null;
  if (piece.kind === "hardBreak") return <br key={`p${piece.id}`} {...attributes} />;
  // One stable keyed wrapper/text node per producer piece. Never concatenate
  // fragments or slice JS text using the producer's UTF-8 source coordinates.
  return (
    <span key={`p${piece.id}`} {...attributes}>
      {piece.text}
    </span>
  );
}

export function markdownWindowContents(
  snapshot: MarkdownRenderSnapshot,
  geometry: MarkdownWindowGeometry | undefined,
  children: Map<number, AssembledMarkdownContext[]>,
  pieces: Map<number, MarkdownTextPiece[]>,
  markdownContextNode: (entry: AssembledMarkdownContext) => VNode,
) {
  function contents(id: number): ComponentChildren {
    const entries: Entry[] = [];
    for (const child of children.get(id) ?? [])
      entries.push({
        at: child.context.firstPiece,
        order: child.context.id,
        node: markdownContextNode(child),
      });
    for (const piece of pieces.get(id) ?? []) {
      const node = pieceNode(piece);
      if (node) entries.push({ at: piece.id, order: Number.MAX_SAFE_INTEGER, node });
    }
    const parent = snapshot.contexts.get(id)?.context;
    if (parent?.kind === "document") {
      const spans = [
        ...(children.get(id) ?? []).map(({ context }) => context),
        ...(pieces.get(id) ?? []).map(({ id: firstPiece }) => ({ firstPiece, pieceCount: 1 })),
      ];
      for (const { from, next } of markdownRootGaps(
        parent.firstPiece,
        parent.firstPiece + parent.pieceCount,
        spans,
      )) {
        const height =
          geometry?.rootGapHeight(from, next, snapshot.generation) ?? (next - from) * 24;
        entries.push({
          at: from,
          order: -1,
          node: (
            <div
              key={`r${from}`}
              data-markdown-gap="root"
              data-gap-from={from}
              data-gap-next={next}
              aria-hidden="true"
              className="markdown-renderer-gap"
              style={{ height: `${height}px` }}
            />
          ),
        });
      }
    } else if (parent && ordinaryGapParents.has(parent.kind)) {
      const spans = [
        ...(children.get(id) ?? []).map(({ context }) => context),
        ...(pieces.get(id) ?? []).map(({ id: firstPiece }) => ({ firstPiece, pieceCount: 1 })),
      ];
      for (const { from, next } of markdownRootGaps(
        parent.firstPiece,
        parent.firstPiece + parent.pieceCount,
        spans,
      )) {
        const height =
          geometry?.rootGapHeight(from, next, snapshot.generation) ?? (next - from) * 24;
        const attributes = {
          key: `o${parent.id}-${from}`,
          "data-markdown-gap": "ordinary",
          "data-gap-from": from,
          "data-gap-next": next,
          "data-context": parent.id,
          "aria-hidden": "true" as const,
          className: "markdown-renderer-gap",
          style: { height: `${height}px` },
        };
        const node =
          parent.kind === "list" ? (
            <li {...attributes} />
          ) : parent.kind === "paragraph" ||
            parent.kind === "heading" ||
            parent.kind === "emphasis" ||
            parent.kind === "strong" ||
            parent.kind === "inlineCode" ||
            parent.kind === "link" ? (
            <span {...attributes} />
          ) : (
            <div {...attributes} />
          );
        entries.push({ at: from, order: -1, node });
      }
    }
    return entries.sort((a, b) => a.at - b.at || a.order - b.order).map((entry) => entry.node);
  }

  // Only complete positional units have gap geometry. Paragraph fragments, cells,
  // metadata and partial producer lines remain the caller's responsibility.
  function windowedChildren(
    parent: AssembledMarkdownContext,
    rows: AssembledMarkdownContext[],
    columns = 1,
  ): ComponentChildren {
    if (geometry)
      requireMarkdownWindowUnits(snapshot, parent, children.get(parent.context.id) ?? []);
    const kind = parent.context.kind === "code" ? "code" : "table";
    const start =
      kind === "table"
        ? (children.get(parent.context.id) ?? []).filter((row) => row.context.header).length
        : 0;
    const result: Entry[] = rows.map((row) => ({
      at: row.context.position,
      order: 0,
      node: markdownContextNode(row),
    }));
    const gaps = markdownChildGaps(
      parent.context.childCount,
      rows.map((row) => row.context.position),
      start,
    );
    for (const { from, next } of gaps) {
      const height =
        geometry?.gapHeight(kind, parent.context.id, from, next, snapshot.generation) ??
        (next - from) * (kind === "code" ? 24 : 40);
      const attributes = {
        key: `g${parent.context.id}-${from}`,
        "data-markdown-gap": kind,
        "data-gap-from": from,
        "data-gap-next": next,
        "aria-hidden": "true" as const,
        className: "markdown-renderer-gap",
      };
      const node =
        kind === "code" ? (
          <span {...attributes} style={{ height: `${height}px` }} />
        ) : (
          <tr {...attributes}>
            <td colSpan={columns} style={{ height: `${height}px` }} aria-hidden="true" />
          </tr>
        );
      result.push({ at: from, order: 0, node });
    }
    return result.sort((a, b) => a.at - b.at).map((entry) => entry.node);
  }

  return { contents, windowedChildren };
}
