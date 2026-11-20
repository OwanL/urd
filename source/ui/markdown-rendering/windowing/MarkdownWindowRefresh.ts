import type { MarkdownRenderSnapshot } from "../MarkdownRenderModel.ts";
import type { MarkdownWindowSurface } from "../MarkdownWindowSurface.tsx";
import type { MarkdownGenerationHandoff } from "./MarkdownGenerationHandoff.ts";
import {
  expandToUnits,
  normalizeIntervals,
  refreshRange,
  sameIntervals,
} from "./MarkdownWindowAlignment.ts";
import type { MarkdownWindowAnchor } from "./MarkdownWindowGeometry.ts";
import { anchorNearCursor } from "./MarkdownWindowPosition.ts";
import { alignAndTrimWindow, fillWindow } from "./MarkdownWindowPreparation.ts";

export async function refreshMarkdownWindow(
  handoff: MarkdownGenerationHandoff,
  viewport: Pick<
    MarkdownWindowSurface,
    "seekTarget" | "watchSeek" | "retainedPins" | "visiblePieces" | "readDirection" | "capture"
  >,
  range: {
    readonly trimOnly: boolean;
    readonly pieceCount: number | undefined;
    readonly retainedPieceCount: number;
  },
  fence: {
    isCurrent(): boolean;
    publish(snapshot: MarkdownRenderSnapshot, anchor: MarkdownWindowAnchor | null): void;
  },
  signal: AbortSignal,
): Promise<MarkdownRenderSnapshot | null> {
  const prior = handoff.snapshot;
  if (prior.from === prior.next) return prior;
  const seek = viewport.seekTarget(prior);
  const { trimOnly, pieceCount, retainedPieceCount } = range;
  if (
    !trimOnly &&
    seek?.generation === prior.generation &&
    pieceCount !== undefined &&
    (!("index" in seek) || (seek.index >= 0 && seek.index < pieceCount))
  ) {
    const currentRetained = () =>
      normalizeIntervals(viewport.retainedPins().map((pin) => expandToUnits(prior, pin, true)));
    const retained = currentRetained();
    const seekStillCurrent = viewport.watchSeek(prior, seek);
    return await handoff.seek(
      seek,
      retainedPieceCount,
      retained,
      async (window, cursor, seekSignal) => {
        const { snapshot, active } = await fillWindow(
          window,
          pieceCount,
          retainedPieceCount,
          retained,
          seekSignal,
        );
        return {
          snapshot,
          anchor: anchorNearCursor(snapshot, cursor, seek.offset, active),
        };
      },
      () => fence.isCurrent() && seekStillCurrent() && sameIntervals(retained, currentRetained()),
      signal,
    );
  }

  const visible = viewport.visiblePieces(prior);
  const direction = viewport.readDirection(prior, visible);
  const retained = viewport.retainedPins();
  const active = trimOnly
    ? { from: prior.from, next: prior.next }
    : refreshRange(
        await (direction === "forward" ? handoff.append(signal) : handoff.prepend(signal)),
        direction,
        visible,
        retainedPieceCount,
      );
  if (signal.aborted || !fence.isCurrent()) return null;
  const desiredDisplay = normalizeIntervals([active, ...retained]);
  if (
    trimOnly &&
    active.from === prior.from &&
    active.next === prior.next &&
    sameIntervals(desiredDisplay, normalizeIntervals(prior.displayRanges))
  )
    return prior;
  const { snapshot: trimmed } = await alignAndTrimWindow(
    handoff,
    active,
    retained,
    retainedPieceCount,
    signal,
  );
  if (signal.aborted || !fence.isCurrent()) return null;
  if (
    trimmed.from === prior.from &&
    trimmed.next === prior.next &&
    trimmed.hasMore === prior.hasMore &&
    sameIntervals(trimmed.displayRanges, normalizeIntervals(prior.displayRanges)) &&
    sameIntervals(trimmed.supportRanges, normalizeIntervals(prior.supportRanges))
  )
    return trimmed;
  fence.publish(trimmed, viewport.capture());
  return trimmed;
}
