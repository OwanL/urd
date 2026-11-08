export interface TabPosition {
  id: string;
  left: number;
  right: number;
}

// Positions exclude the tab being moved and use viewport coordinates.
export function tabInsertion(positions: readonly TabPosition[], x: number): string | null {
  return positions.find((position) => x < (position.left + position.right) / 2)?.id ?? null;
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
