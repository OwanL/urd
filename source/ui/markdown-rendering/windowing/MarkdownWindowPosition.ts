import type {
  MarkdownPieceInterval,
  MarkdownRenderCursor,
  MarkdownRenderSnapshot,
} from "../MarkdownRenderModel.ts";
import type { MarkdownWindowAnchor } from "./MarkdownWindowGeometry.ts";

export function anchorNearCursor(
  snapshot: MarkdownRenderSnapshot,
  cursor: MarkdownRenderCursor,
  offset: number,
  preferred?: MarkdownPieceInterval,
): MarkdownWindowAnchor | null {
  const pieces = [...snapshot.pieces.values()].filter(
    (piece) => piece.text || snapshot.contexts.get(piece.context)?.context.kind === "image",
  );
  const orderByDistance = (left: (typeof pieces)[number], right: (typeof pieces)[number]) =>
    Math.abs(left.id - cursor.index) - Math.abs(right.id - cursor.index);
  const piece =
    (preferred
      ? pieces
          .filter((candidate) => preferred.from <= candidate.id && candidate.id < preferred.next)
          .sort(orderByDistance)[0]
      : undefined) ?? pieces.sort(orderByDistance)[0];
  if (!piece) return null;
  return {
    generation: snapshot.generation,
    piece: String(piece.id),
    start: String(piece.source.start),
    end: String(piece.source.end),
    offset,
  };
}
