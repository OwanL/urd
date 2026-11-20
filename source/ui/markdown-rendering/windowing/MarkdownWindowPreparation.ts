import type { MessageContentCursor } from "../../sessions/transcript/Messages.ts";
import type { MarkdownOwnedTextGeneration } from "../MarkdownClient.ts";
import type {
  MarkdownPageSource,
  MarkdownPieceInterval,
  MarkdownRenderCursor,
  MarkdownRenderSnapshot,
} from "../MarkdownRenderModel.ts";
import { alignWindow, initialFrom } from "./MarkdownWindowAlignment.ts";
import { MarkdownWindowAssembler } from "./MarkdownWindowAssembler.ts";

type InitialWindowSource = MarkdownPageSource &
  Pick<MarkdownOwnedTextGeneration, "manifest" | "source" | "locateSource">;

export async function prepareInitialWindow(
  source: InitialWindowSource,
  savedCursor: MessageContentCursor | undefined,
  retainedPieceCount: number,
  signal: AbortSignal,
): Promise<{ window: MarkdownWindowAssembler; location: MarkdownRenderCursor }> {
  signal.throwIfAborted();
  const location = await source.locateSource(savedCursor ?? source.source.availableThrough, signal);
  signal.throwIfAborted();
  const manifest = source.manifest;
  if (
    !location ||
    location.generation !== manifest.generation ||
    !Number.isSafeInteger(location.index) ||
    location.index < 0 ||
    (manifest.pieceCount === 0 ? location.index !== 0 : location.index >= manifest.pieceCount)
  )
    throw new Error("Markdown source cursor does not match its render generation.");

  const from = initialFrom(location.index, manifest.pieceCount, retainedPieceCount);
  const window = new MarkdownWindowAssembler(source, manifest, from);
  await fillWindow(window, manifest.pieceCount, retainedPieceCount, [], signal);
  return { window, location };
}

export async function fillWindow(
  window: MarkdownWindowAssembler,
  pieceCount: number,
  retainedPieceCount: number,
  retain: readonly MarkdownPieceInterval[],
  signal: AbortSignal,
): Promise<{ snapshot: MarkdownRenderSnapshot; active: MarkdownPieceInterval }> {
  const from = window.snapshot.from;
  const next = Math.min(pieceCount, from + retainedPieceCount);
  while (window.snapshot.next < next) {
    signal.throwIfAborted();
    await window.append(signal);
  }
  return alignAndTrimWindow(window, { from, next }, retain, retainedPieceCount, signal);
}

export async function alignAndTrimWindow(
  window: Pick<MarkdownWindowAssembler, "snapshot" | "append" | "prepend" | "trim">,
  active: MarkdownPieceInterval,
  retain: readonly MarkdownPieceInterval[],
  retainedPieceCount: number,
  signal: AbortSignal,
): Promise<{ snapshot: MarkdownRenderSnapshot; active: MarkdownPieceInterval }> {
  const aligned = await alignWindow(
    window,
    window.snapshot,
    active,
    retain,
    retainedPieceCount,
    signal,
  );
  const snapshot = await window.trim(
    { generation: aligned.snapshot.generation, active: aligned.active, retain: aligned.retain },
    signal,
  );
  return { snapshot, active: aligned.active };
}
