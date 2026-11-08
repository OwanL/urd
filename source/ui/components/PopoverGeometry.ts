export type PopoverPlacement = "above" | "below";

interface AnchorBounds {
  left: number;
  top: number;
  bottom: number;
}

// Keep geometry independent of the DOM so clipped anchors and small viewports stay testable.
export function placePopover(
  anchor: AnchorBounds,
  size: { width: number; height: number },
  viewport: { width: number; height: number },
  preferred: PopoverPlacement = "below",
) {
  const margin = 8;
  const gap = 6;
  const maxWidth = Math.max(0, viewport.width - margin * 2);
  const viewportHeight = Math.max(0, viewport.height - margin * 2);
  const above = Math.min(viewportHeight, Math.max(0, anchor.top - gap - margin));
  const below = Math.min(
    viewportHeight,
    Math.max(0, viewport.height - margin - anchor.bottom - gap),
  );
  const space = { above, below };
  const opposite = preferred === "above" ? "below" : "above";
  const placement =
    size.height > space[preferred] && space[opposite] > space[preferred] ? opposite : preferred;
  const maxHeight = space[placement];
  const width = Math.min(size.width, maxWidth);
  const height = Math.min(size.height, maxHeight);
  const top = placement === "above" ? anchor.top - gap - height : anchor.bottom + gap;
  const leftEdge = Math.min(margin, viewport.width / 2);
  const topEdge = Math.min(margin, viewport.height / 2);
  return {
    placement,
    left: Math.max(leftEdge, Math.min(anchor.left, viewport.width - margin - width)),
    top: Math.max(topEdge, Math.min(top, viewport.height - margin - height)),
    maxWidth,
    maxHeight,
  };
}
