import { createElement, type ComponentChildren, type VNode } from "preact";
import { useId } from "preact/hooks";
import type {
  AssembledMarkdownContext,
  MarkdownRenderSnapshot,
  MarkdownSourceRange,
  MarkdownTextPiece,
} from "./MarkdownRenderModel.ts";
import { safeMarkdownLink } from "./MarkdownLinkPolicy.ts";
import {
  markdownChildGaps,
  markdownRootGaps,
  markdownTableColumns,
  requireMarkdownWindowUnits,
  type MarkdownWindowGeometry,
} from "./MarkdownWindowGeometry.ts";
import "./markdown-renderer.css";

type Entry = { at: number; order: number; node: VNode };

function rangeAttributes(source: MarkdownSourceRange) {
  return { "data-source-start": source.start, "data-source-end": source.end };
}

function pieceNode(piece: MarkdownTextPiece) {
  const attributes = {
    ...rangeAttributes(piece.source),
    "data-piece": piece.id,
    "data-part": piece.part,
    "data-continued": piece.continued,
    "data-mapping": piece.mapping,
    "data-text-kind": piece.kind,
  };
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

// Deliberately unactivated: callers must provide an assembled, normalized window.
export function MarkdownRenderer({
  snapshot,
  geometry,
}: {
  snapshot: MarkdownRenderSnapshot;
  geometry?: MarkdownWindowGeometry | undefined;
}) {
  const instance = useId();
  const children = new Map<number, AssembledMarkdownContext[]>();
  const pieces = new Map<number, MarkdownTextPiece[]>();
  for (const entry of snapshot.contexts.values()) {
    const siblings = children.get(entry.context.parent) ?? [];
    siblings.push(entry);
    children.set(entry.context.parent, siblings);
  }
  for (const piece of snapshot.pieces.values()) {
    const siblings = pieces.get(piece.context) ?? [];
    siblings.push(piece);
    pieces.set(piece.context, siblings);
  }
  // Instance-local IDs stay valid IDREF tokens even when generation contains spaces.
  const headerId = (id: number) => `${instance}-header-${id}`;

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

  function markdownContextNode(entry: AssembledMarkdownContext): VNode {
    const { context, values } = entry;
    const key = `c${context.id}`;
    const parent = snapshot.contexts.get(context.parent);
    const rootPieceAttributes =
      parent?.context.kind === "document" ? { "data-root-piece-count": context.pieceCount } : {};
    const attributes = {
      ...rangeAttributes(context.source),
      ...rootPieceAttributes,
      "data-context": context.id,
      "data-kind": context.kind,
      "data-position": context.position,
      "data-mapping": "container",
    };
    // Tables group rows below; do not construct their entire subtree twice.
    const content =
      context.kind === "table" || context.kind === "code" ? null : contents(context.id);
    switch (context.kind) {
      case "document":
        return (
          <div key={key} {...attributes}>
            {content}
          </div>
        );
      case "paragraph":
        return (
          <p key={key} {...attributes}>
            {content}
          </p>
        );
      case "heading":
        return createElement(
          `h${Math.min(6, Math.max(1, context.level))}`,
          { ...attributes, key },
          content,
        );
      case "quote":
        return (
          <blockquote key={key} {...attributes}>
            {content}
          </blockquote>
        );
      case "list": {
        const start = orderedStart(values.orderedStart);
        return context.ordered ? (
          <ol key={key} {...attributes} start={start} data-loose={context.loose}>
            {content}
          </ol>
        ) : (
          <ul key={key} {...attributes} data-loose={context.loose}>
            {content}
          </ul>
        );
      }
      case "item":
        return (
          <li
            key={key}
            {...attributes}
            value={
              parent?.context.ordered
                ? orderedStart(parent.values.orderedStart) + context.position
                : undefined
            }
          >
            {content}
          </li>
        );
      case "code":
        return (
          <pre key={key} {...attributes} tabIndex={0} aria-label="Code">
            <code data-language={values.language}>
              {windowedChildren(entry, children.get(context.id) ?? [])}
            </code>
          </pre>
        );
      case "codeLine":
        return (
          <span key={key} {...attributes}>
            {content}
          </span>
        );
      case "inlineCode":
        return (
          <code key={key} {...attributes}>
            {content}
          </code>
        );
      case "emphasis":
        return (
          <em key={key} {...attributes}>
            {content}
          </em>
        );
      case "strong":
        return (
          <strong key={key} {...attributes}>
            {content}
          </strong>
        );
      case "link": {
        const href = safeMarkdownLink(values.destination);
        return href === undefined ? (
          <span key={key} {...attributes}>
            {content}
          </span>
        ) : (
          <a key={key} {...attributes} href={href} title={values.title}>
            {content}
          </a>
        );
      }
      case "thematicBreak":
        return <hr key={key} {...attributes} />;
      case "table": {
        const rows = [...(children.get(context.id) ?? [])].sort(
          (a, b) => a.context.position - b.context.position,
        );
        const headers = rows.filter((row) => row.context.header);
        const headerCells = headers.flatMap((row) => children.get(row.context.id) ?? []);
        const lengths = new Map<number, number>();
        for (const { context: cell } of headerCells) {
          let length = 0;
          for (let id = cell.firstPiece; id < cell.firstPiece + cell.pieceCount; id++)
            length += snapshot.pieces.get(id)?.text.length ?? 0;
          lengths.set(cell.id, length);
        }
        const widths = markdownTableColumns(headerCells, lengths);
        return (
          <div
            key={key}
            className="markdown-renderer-table"
            {...rootPieceAttributes}
            tabIndex={0}
            role="region"
            aria-label="Table"
          >
            <table {...attributes}>
              <colgroup>
                {widths.map((width, position) => (
                  <col key={position} style={{ width: `${width}%` }} />
                ))}
              </colgroup>
              <thead>{headers.map(markdownContextNode)}</thead>
              <tbody>
                {windowedChildren(
                  entry,
                  rows.filter((row) => !row.context.header),
                  widths.length,
                )}
              </tbody>
            </table>
          </div>
        );
      }
      case "row":
        return (
          <tr key={key} {...attributes}>
            {content}
          </tr>
        );
      case "cell": {
        const style = context.alignment === "none" ? undefined : { textAlign: context.alignment };
        return context.header ? (
          <th key={key} {...attributes} id={headerId(context.id)} scope="col" style={style}>
            {content}
          </th>
        ) : (
          <td
            key={key}
            {...attributes}
            headers={context.headerCell >= 0 ? headerId(context.headerCell) : undefined}
            style={style}
          >
            {content}
          </td>
        );
      }
    }
  }

  return (
    <div
      className="markdown-renderer"
      key={snapshot.generation}
      data-markdown-generation={snapshot.generation}
    >
      {(children.get(-1) ?? [])
        .sort((a, b) => a.context.id - b.context.id)
        .map(markdownContextNode)}
    </div>
  );
}

function orderedStart(value: string | undefined): number {
  if (value === undefined || !/^\d{1,9}$/u.test(value)) return 1;
  return Number(value);
}
