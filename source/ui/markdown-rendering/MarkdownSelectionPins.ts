import type { MarkdownPieceInterval, MarkdownRenderSnapshot } from "./MarkdownRenderModel.ts";

type SelectionSnapshot = Pick<
  MarkdownRenderSnapshot,
  "generation" | "displayRanges" | "supportRanges"
>;

export class MarkdownSelectionPins {
  private readonly root: HTMLElement;
  private readonly generation: string;
  private readonly snapshot: () => SelectionSnapshot;
  private readonly changed: () => void;
  private disposed = false;

  constructor(root: HTMLElement, snapshot: () => SelectionSnapshot, changed: () => void) {
    this.root = root;
    this.snapshot = snapshot;
    this.generation = snapshot().generation;
    this.changed = changed;
    root.ownerDocument.addEventListener("selectionchange", this.onSelectionChange);
    root.ownerDocument.addEventListener("focusin", this.onSelectionChange);
    root.ownerDocument.addEventListener("focusout", this.onSelectionChange);
  }

  read(): readonly MarkdownPieceInterval[] {
    if (this.disposed) return [];
    const snapshot = this.snapshot();
    if (snapshot.generation !== this.generation) return [];

    const selection = this.root.ownerDocument.getSelection();
    if (!selection?.rangeCount) return [];

    const available = [...snapshot.displayRanges, ...snapshot.supportRanges];
    const pieces = this.root.querySelectorAll<HTMLElement>("[data-piece]");
    const intervals: MarkdownPieceInterval[] = [];
    for (let index = 0; index < selection.rangeCount; index++) {
      const range = selection.getRangeAt(index);
      if (!range.intersectsNode(this.root)) continue;
      const ids = new Set<number>();
      for (const piece of pieces) {
        const id = Number(piece.dataset.piece);
        if (
          Number.isSafeInteger(id) &&
          available.some(({ from, next }) => from <= id && id < next) &&
          range.intersectsNode(piece)
        )
          ids.add(id);
      }
      if (selection.isCollapsed) {
        const focus = this.pieceAt(selection.focusNode);
        if (focus !== null && available.some(({ from, next }) => from <= focus && focus < next))
          ids.add(focus);
      }
      for (const { from, next } of available) {
        let first = next;
        let last = from - 1;
        for (const id of ids)
          if (from <= id && id < next) {
            first = Math.min(first, id);
            last = Math.max(last, id);
          }
        if (last >= from) intervals.push({ from: first, next: last + 1 });
      }
    }
    intervals.sort((a, b) => a.from - b.from);
    return intervals.reduce<MarkdownPieceInterval[]>((normalized, interval) => {
      const last = normalized.at(-1);
      if (!last || interval.from > last.next) normalized.push(interval);
      else
        normalized[normalized.length - 1] = {
          from: last.from,
          next: Math.max(last.next, interval.next),
        };
      return normalized;
    }, []);
  }

  // Native selection (including collapsed caret) and keyboard focus defer catch-up.
  // There is no selection-size cap; a full unmount still loses native selection.
  blocked(): boolean {
    return (
      !this.disposed &&
      (this.contains(this.root.ownerDocument.activeElement) || this.read().length > 0)
    );
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.root.ownerDocument.removeEventListener("selectionchange", this.onSelectionChange);
    this.root.ownerDocument.removeEventListener("focusin", this.onSelectionChange);
    this.root.ownerDocument.removeEventListener("focusout", this.onSelectionChange);
  }

  private readonly onSelectionChange = (): void => {
    // focusout fires before activeElement settles; recheck without retaining an event.
    queueMicrotask(() => {
      if (!this.disposed) this.changed();
    });
  };

  private contains(node: Node | null): node is Node {
    return node !== null && this.root.contains(node);
  }

  private pieceAt(node: Node | null): number | null {
    if (!this.contains(node)) return null;
    const element = node.nodeType === Node.ELEMENT_NODE ? (node as Element) : node.parentElement;
    const piece = element?.closest<HTMLElement>("[data-piece]");
    if (!piece || !this.root.contains(piece)) return null;
    const id = Number(piece.dataset.piece);
    return Number.isSafeInteger(id) ? id : null;
  }
}
