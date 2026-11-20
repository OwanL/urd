// Aligns retained Markdown windows to complete units and selects their piece ranges.
import type { MarkdownPieceInterval, MarkdownRenderSnapshot } from "../MarkdownRenderModel.ts";
import type { MarkdownWindowAssembler } from "./MarkdownWindowAssembler.ts";

export async function alignWindow(
  window: Pick<MarkdownWindowAssembler, "snapshot" | "append" | "prepend">,
  initial: MarkdownRenderSnapshot,
  active: MarkdownPieceInterval,
  retain: readonly MarkdownPieceInterval[],
  retainedPieceCount: number,
  signal: AbortSignal,
): Promise<{
  snapshot: MarkdownRenderSnapshot;
  active: MarkdownPieceInterval;
  retain: readonly MarkdownPieceInterval[];
}> {
  let snapshot = initial;
  let activeRange = active;
  let retainedRanges = normalizeIntervals(retain);
  for (let attempt = 0; attempt < 8; attempt++) {
    signal.throwIfAborted();
    // A window can start inside a header without a body row to hydrate it as support.
    const expandedActive = expandToUnits(snapshot, activeRange);
    const expandedRetain = normalizeIntervals(
      retainedRanges.map((range) => expandToUnits(snapshot, range)),
    );
    if (expandedActive.next - expandedActive.from > retainedPieceCount * 3)
      throw new Error(
        "A complete Markdown code line or table row exceeds the active window bound.",
      );
    activeRange = expandedActive;
    retainedRanges = expandedRetain;

    while (snapshot.from > activeRange.from) {
      signal.throwIfAborted();
      snapshot = await window.prepend(signal);
    }
    while (snapshot.next < activeRange.next) {
      signal.throwIfAborted();
      snapshot = await window.append(signal);
    }
    for (const range of retainedRanges)
      for (let id = range.from; id < range.next; id++)
        if (!snapshot.pieces.has(id))
          throw new Error("A Markdown selection pin is outside the loaded window.");

    const checkedActive = expandToUnits(snapshot, activeRange);
    const checkedRetain = normalizeIntervals(
      retainedRanges.map((range) => expandToUnits(snapshot, range)),
    );
    if (
      checkedActive.from === activeRange.from &&
      checkedActive.next === activeRange.next &&
      sameIntervals(checkedRetain, retainedRanges)
    )
      return { snapshot, active: activeRange, retain: retainedRanges };
    activeRange = checkedActive;
    retainedRanges = checkedRetain;
  }
  throw new Error("Markdown window did not align to complete code lines and table rows.");
}

export function initialFrom(index: number, pieceCount: number, retainedPieceCount: number): number {
  const start = Math.max(0, index - Math.floor(retainedPieceCount / 2));
  return Math.min(start, Math.max(0, pieceCount - retainedPieceCount));
}

export function expandToUnits(
  snapshot: MarkdownRenderSnapshot,
  range: MarkdownPieceInterval,
): MarkdownPieceInterval {
  let from = range.from;
  let next = range.next;
  for (const { context } of snapshot.contexts.values()) {
    if ((context.kind !== "codeLine" && context.kind !== "row") || context.pieceCount === 0)
      continue;
    const unitNext = context.firstPiece + context.pieceCount;
    if (context.firstPiece < next && unitNext > from) {
      from = Math.min(from, context.firstPiece);
      next = Math.max(next, unitNext);
    }
  }
  return { from, next };
}

export function normalizeIntervals(
  intervals: readonly MarkdownPieceInterval[],
): MarkdownPieceInterval[] {
  const sorted = [...intervals]
    .filter(
      ({ from, next }) => Number.isSafeInteger(from) && Number.isSafeInteger(next) && next > from,
    )
    .sort((left, right) => left.from - right.from);
  const result: MarkdownPieceInterval[] = [];
  for (const interval of sorted) {
    const last = result.at(-1);
    if (!last || interval.from > last.next) result.push({ ...interval });
    else result[result.length - 1] = { from: last.from, next: Math.max(last.next, interval.next) };
  }
  return result;
}

export function sameIntervals(
  left: readonly MarkdownPieceInterval[],
  right: readonly MarkdownPieceInterval[],
): boolean {
  return (
    left.length === right.length &&
    left.every(
      (range, index) => range.from === right[index]?.from && range.next === right[index]?.next,
    )
  );
}

export function refreshRange(
  loaded: MarkdownRenderSnapshot,
  direction: "forward" | "backward",
  visible: MarkdownPieceInterval | null,
  retainedPieceCount: number,
): MarkdownPieceInterval {
  if (direction === "forward") {
    let from = Math.max(loaded.from, loaded.next - retainedPieceCount);
    if (visible && visible.from >= loaded.from && visible.from < loaded.next)
      from = Math.min(from, visible.from);
    let next = Math.min(loaded.next, from + retainedPieceCount);
    if (visible && visible.next > next) {
      next = visible.next;
      from = Math.max(loaded.from, next - retainedPieceCount);
    }
    return { from, next };
  }

  let from = loaded.from;
  let next = Math.min(loaded.next, from + retainedPieceCount);
  if (visible && visible.next > next) {
    next = visible.next;
    from = Math.max(loaded.from, next - retainedPieceCount);
  }
  if (visible && visible.from < from) {
    from = visible.from;
    next = Math.min(loaded.next, from + retainedPieceCount);
  }
  return { from, next };
}
