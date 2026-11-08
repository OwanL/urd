export interface TabPosition {
  id: string;
  left: number;
  right: number;
}

// Geometry stays in the original layout. Only the occupied slot changes.
export function tabDragOffset(
  positions: readonly TabPosition[],
  from: number,
  to: number,
  index: number,
): number {
  const tab = positions[from]!;
  const gap = positions.length > 1 ? positions[1]!.left - positions[0]!.right : 0;
  const distance = tab.right - tab.left + gap;
  if (from < to && index > from && index <= to) return -distance;
  if (from > to && index >= to && index < from) return distance;
  return 0;
}

export function tabDragIndex(
  positions: readonly TabPosition[],
  from: number,
  to: number,
  pointer: number,
  grab: number,
): number {
  const left = pointer - grab;
  const width = positions[from]!.right - positions[from]!.left;
  const center = (index: number) => {
    const tab = positions[index]!;
    return (tab.left + tab.right) / 2 + tabDragOffset(positions, from, to, index);
  };
  // Test the facing edge against the neighbor's current slot, not its animated bounds.
  while (to < positions.length - 1 && left + width > center(to < from ? to : to + 1)) to++;
  while (to > 0 && left < center(to <= from ? to - 1 : to)) to--;
  return to;
}

// Keep item identity and return the original order for invalid or unchanged moves.
export function moveTab<T extends { id: string }>(
  tabs: readonly T[],
  id: string,
  beforeId: string | null,
): readonly T[] {
  const item = tabs.find((tab) => tab.id === id);
  if (!item || id === beforeId || (beforeId !== null && !tabs.some((tab) => tab.id === beforeId)))
    return tabs;
  const next = tabs.filter((tab) => tab.id !== id);
  next.splice(
    beforeId === null ? next.length : next.findIndex((tab) => tab.id === beforeId),
    0,
    item,
  );
  return next.every((tab, index) => tab === tabs[index]) ? tabs : next;
}
