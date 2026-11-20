import type { TranscriptPositionWindow } from "../../../sessions/transcript/Messages.ts";
import type { TranscriptNavigationState } from "../navigation/TranscriptNavigationStorage.ts";
import {
  transcriptNavigationProfileVersion,
  type TranscriptNavigationStore,
} from "../navigation/useTranscriptNavigation.ts";
import { sameEntry } from "./TranscriptRows.ts";
import type { TranscriptViewport } from "./TranscriptViewport.ts";

export function readViewportNavigation(
  model: TranscriptViewport,
  width: number,
): TranscriptNavigationState {
  const { historyRevision, positionVersion, followsLatest, anchor } = model.snapshot();
  return {
    historyRevision,
    positionVersion,
    followsLatest,
    anchor: anchor && {
      entry: anchor.entry,
      sourceCursor: anchor.sourceCursor,
      viewportOffset: anchor.viewportOffset,
    },
    width: width || null,
    profileVersion: transcriptNavigationProfileVersion,
  };
}

export function restoreInitialWindowAnchor(
  model: TranscriptViewport,
  next: TranscriptPositionWindow,
  width: number,
  load: TranscriptNavigationStore["load"],
  restoreAnchor: (anchor: TranscriptNavigationState["anchor"]) => void,
): void {
  const { state: saved, pixelReuse } = load(width).value;
  if (
    !saved ||
    saved.historyRevision !== next.state.historyRevision ||
    saved.followsLatest ||
    !saved.anchor ||
    !next.target ||
    !sameEntry(saved.anchor.entry, next.target)
  ) {
    model.jumpToLatest();
    return;
  }
  const viewportOffset = pixelReuse ? saved.anchor.viewportOffset : 0;
  if (
    model.jumpToAnchor({
      entry: next.target,
      rank: next.target.rank,
      sourceCursor: saved.anchor.sourceCursor,
      viewportOffset,
    })
  )
    restoreAnchor(saved.anchor.sourceCursor ? { ...saved.anchor, viewportOffset } : null);
}
