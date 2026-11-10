import { createElement, type ComponentChildren, type VNode } from "preact";
import { useId } from "preact/hooks";
import type {
  AssembledContext,
  DisplayPiece,
  DisplaySnapshot,
  SourceRange,
} from "./DisplayModel.ts";
import { safeDisplayLink } from "./DisplayLink.ts";
import "./semantic-display.css";

type Entry = { at: number; order: number; node: VNode };

function rangeAttributes(source: SourceRange) {
  return { "data-source-start": source.start, "data-source-end": source.end };
}

function pieceNode(piece: DisplayPiece) {
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
export function SemanticDisplay({ snapshot }: { snapshot: DisplaySnapshot }) {
  const instance = useId();
  const children = new Map<number, AssembledContext[]>();
  const pieces = new Map<number, DisplayPiece[]>();
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
        node: contextNode(child),
      });
    for (const piece of pieces.get(id) ?? []) {
      const node = pieceNode(piece);
      if (node) entries.push({ at: piece.id, order: Number.MAX_SAFE_INTEGER, node });
    }
    return entries.sort((a, b) => a.at - b.at || a.order - b.order).map((entry) => entry.node);
  }

  function contextNode({ context, values }: AssembledContext): VNode {
    const attributes = {
      key: `c${context.id}`,
      ...rangeAttributes(context.source),
      "data-context": context.id,
      "data-kind": context.kind,
      "data-mapping": "container",
    };
    // Tables group rows below; do not construct their entire subtree twice.
    const content = context.kind === "table" ? null : contents(context.id);
    const parent = snapshot.contexts.get(context.parent);
    switch (context.kind) {
      case "document":
        return <div {...attributes}>{content}</div>;
      case "paragraph":
        return <p {...attributes}>{content}</p>;
      case "heading":
        return createElement(`h${Math.min(6, Math.max(1, context.level))}`, attributes, content);
      case "quote":
        return <blockquote {...attributes}>{content}</blockquote>;
      case "list": {
        const start = orderedStart(values.orderedStart);
        return context.ordered ? (
          <ol {...attributes} start={start} data-loose={context.loose}>
            {content}
          </ol>
        ) : (
          <ul {...attributes} data-loose={context.loose}>
            {content}
          </ul>
        );
      }
      case "item":
        return (
          <li
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
          <pre {...attributes} tabIndex={0} aria-label="Code">
            <code data-language={values.language}>{content}</code>
          </pre>
        );
      case "codeLine":
        return <span {...attributes}>{content}</span>;
      case "inlineCode":
        return <code {...attributes}>{content}</code>;
      case "emphasis":
        return <em {...attributes}>{content}</em>;
      case "strong":
        return <strong {...attributes}>{content}</strong>;
      case "link": {
        const href = safeDisplayLink(values.destination);
        return href === undefined ? (
          <span {...attributes}>{content}</span>
        ) : (
          <a {...attributes} href={href} title={values.title}>
            {content}
          </a>
        );
      }
      case "thematicBreak":
        return <hr {...attributes} />;
      case "table": {
        const rows = [...(children.get(context.id) ?? [])].sort(
          (a, b) => a.context.position - b.context.position,
        );
        return (
          <div
            key={`c${context.id}`}
            className="semantic-display-table"
            tabIndex={0}
            role="region"
            aria-label="Table"
          >
            <table {...attributes}>
              <thead>{rows.filter((row) => row.context.header).map(contextNode)}</thead>
              <tbody>{rows.filter((row) => !row.context.header).map(contextNode)}</tbody>
            </table>
          </div>
        );
      }
      case "row":
        return <tr {...attributes}>{content}</tr>;
      case "cell": {
        const style = context.alignment === "none" ? undefined : { textAlign: context.alignment };
        return context.header ? (
          <th {...attributes} id={headerId(context.id)} scope="col" style={style}>
            {content}
          </th>
        ) : (
          <td
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
    <div className="semantic-display" key={snapshot.generation}>
      {(children.get(-1) ?? []).sort((a, b) => a.context.id - b.context.id).map(contextNode)}
    </div>
  );
}

function orderedStart(value: string | undefined): number {
  if (value === undefined || !/^\d{1,9}$/u.test(value)) return 1;
  return Number(value);
}
