import { createElement, type VNode } from "preact";
import { useId } from "preact/hooks";
import type { MarkdownImageResource } from "./images/MarkdownImageWindow.ts";
import { MarkdownInlineImage, markdownImageFallback } from "./images/MarkdownInlineImage.tsx";
import type {
  AssembledMarkdownContext,
  MarkdownRenderSnapshot,
  MarkdownSourceRange,
  MarkdownTextPiece,
} from "./MarkdownRenderModel.ts";
import { safeMarkdownLink } from "./MarkdownLinkPolicy.ts";
import {
  markdownTableColumns,
  type MarkdownWindowGeometry,
} from "./windowing/MarkdownWindowGeometry.ts";
import { markdownWindowContents } from "./MarkdownWindowContents.tsx";
import "./markdown-renderer.css";

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

// Deliberately unactivated: callers must provide an assembled, normalized window.
export function MarkdownRenderer({
  snapshot,
  geometry,
  images,
}: {
  snapshot: MarkdownRenderSnapshot;
  geometry?: MarkdownWindowGeometry | undefined;
  images?: { resources: ReadonlyMap<number, MarkdownImageResource>; scrollport: HTMLElement };
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

  const { contents, windowedChildren } = markdownWindowContents(
    snapshot,
    geometry,
    children,
    pieces,
    markdownContextNode,
  );

  function hasLinkAncestor(id: number): boolean {
    let ancestor = snapshot.contexts.get(id);
    while (ancestor) {
      if (ancestor.context.kind === "link") return true;
      ancestor = snapshot.contexts.get(ancestor.context.parent);
    }
    return false;
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
    // These contexts render their pieces or children directly.
    const content =
      context.kind === "table" || context.kind === "code" || context.kind === "image"
        ? null
        : contents(context.id);
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
      case "image": {
        const altPieces = (pieces.get(context.id) ?? []).sort((a, b) => a.id - b.id);
        const firstPiece = altPieces[0];
        const alt = altPieces.map((piece) => piece.text).join("");
        const resource = images?.resources.get(context.id);
        return (
          <span key={key} {...attributes}>
            {firstPiece && (
              <span {...pieceAttributes(firstPiece)} data-markdown-image-piece="">
                {resource && images ? (
                  <MarkdownInlineImage
                    alt={alt}
                    resource={resource}
                    scrollport={images.scrollport}
                    canOpen={!hasLinkAncestor(context.parent)}
                  />
                ) : (
                  markdownImageFallback(alt)
                )}
              </span>
            )}
          </span>
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
