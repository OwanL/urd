import type { AssembledMarkdownContext, MarkdownRenderSnapshot } from "./MarkdownRenderModel.ts";

// Disposable browser geometry for complete code lines and table body rows only.
// The scrollport defaults to the renderer root for standalone, self-scrolling callers.
// A caller owns scrolling: capture before mutation, render, measure/re-render, restore.
// Measurement replaces a shell estimate; it never accumulates historical corrections.
export interface MarkdownWindowAnchor {
  readonly generation: string;
  readonly piece: string;
  readonly start: string;
  readonly end: string;
  readonly offset: number;
}
export type MarkdownWindowSeekTarget =
  | { readonly generation: string; readonly index: number; readonly offset: number }
  | {
      readonly generation: string;
      readonly contextId: number;
      readonly childIndex: number;
      readonly offset: number;
    };

type ShellKind = "code" | "table";
type Estimate = { kind: ShellKind; height: number };

const defaultRootPieceHeight = 24;

export interface MarkdownPieceSpan {
  readonly firstPiece: number;
  readonly pieceCount: number;
}

export function markdownRootGaps(
  firstPiece: number,
  nextPiece: number,
  children: readonly MarkdownPieceSpan[],
): readonly { from: number; next: number }[] {
  const gaps: { from: number; next: number }[] = [];
  let cursor = firstPiece;
  const spans = [...children]
    .filter(
      ({ firstPiece: childFirst, pieceCount }) =>
        pieceCount > 0 && childFirst < nextPiece && childFirst + pieceCount > firstPiece,
    )
    .sort((left, right) => left.firstPiece - right.firstPiece);
  for (const child of spans) {
    const from = Math.max(firstPiece, child.firstPiece);
    const next = Math.min(nextPiece, child.firstPiece + child.pieceCount);
    if (next <= cursor) continue;
    if (from > cursor) gaps.push({ from: cursor, next: from });
    cursor = next;
  }
  if (cursor < nextPiece) gaps.push({ from: cursor, next: nextPiece });
  return gaps;
}

// Positions are ordinals among all direct children, including table headers.
// Only absence of complete siblings is represented: no fabricated source ranges.
export function markdownChildGaps(
  childCount: number,
  positions: readonly number[],
  from = 0,
): readonly { from: number; next: number }[] {
  const gaps: { from: number; next: number }[] = [];
  let next = from;
  for (const position of [...new Set(positions)].sort((a, b) => a - b)) {
    if (position < from || position >= childCount) continue;
    if (position > next) gaps.push({ from: next, next: position });
    next = position + 1;
  }
  if (next < childCount) gaps.push({ from: next, next: childCount });
  return gaps;
}

// Opt-in geometry cannot silently turn a partial producer line/cell into a full unit.
// The owner must align reads and selection retention to these complete intervals.
export function requireMarkdownWindowUnits(
  snapshot: MarkdownRenderSnapshot,
  parent: AssembledMarkdownContext,
  units: readonly AssembledMarkdownContext[],
): void {
  if (parent.context.kind !== "code" && parent.context.kind !== "table")
    throw new Error("Markdown geometry only supports code and table shells.");
  const kind = parent.context.kind === "code" ? "codeLine" : "row";
  const positions = new Set<number>();
  for (const { context } of units) {
    if (
      context.kind !== kind ||
      context.parent !== parent.context.id ||
      context.position >= parent.context.childCount ||
      positions.has(context.position)
    )
      throw new Error("Markdown geometry requires unique, positional code lines or table rows.");
    positions.add(context.position);
    for (let id = context.firstPiece; id < context.firstPiece + context.pieceCount; id++)
      if (!snapshot.pieces.has(id))
        throw new Error("Markdown geometry requires complete code lines and table rows.");
    if (kind !== "row") continue;
    const cells = [...snapshot.contexts.values()].filter(
      (entry) => entry.context.parent === context.id,
    );
    if (cells.length !== context.childCount)
      throw new Error("Markdown geometry requires complete table row structure.");
    const columns = new Set<number>();
    for (const { context: cell } of cells) {
      if (
        cell.kind !== "cell" ||
        cell.position >= context.childCount ||
        columns.has(cell.position) ||
        cell.header !== context.header
      )
        throw new Error("Markdown geometry requires unique, positional table cells.");
      columns.add(cell.position);
    }
  }
}

export function markdownTableColumns(
  cells: readonly AssembledMarkdownContext[],
  lengths: ReadonlyMap<number, number>,
): readonly number[] {
  const count = Math.max(1, ...cells.map(({ context }) => context.position + 1));
  // Complete header text determines weights, never the currently mounted body.
  // Clamp extremes so one long heading cannot make sibling columns unusable.
  const weights = Array.from({ length: count }, (_, position) => {
    const cell = cells.find(({ context }) => context.position === position);
    return Math.min(32, Math.max(4, cell ? (lengths.get(cell.context.id) ?? 0) : 0));
  });
  const total = weights.reduce((sum, weight) => sum + weight, 0);
  return weights.map((weight) => (weight / total) * 100);
}

export class MarkdownWindowGeometry {
  private generation = "";
  private width = 0;
  private profile = "";
  private rootPieceHeight = defaultRootPieceHeight;
  private readonly estimates = new Map<number, Estimate>();

  configure(generation: string, width: number, profile: string): void {
    if (!generation || !Number.isFinite(width) || width <= 0)
      throw new Error("Markdown geometry requires a generation and a positive layout width.");
    if (generation === this.generation && width === this.width && profile === this.profile) return;
    this.generation = generation;
    this.width = width;
    this.profile = profile;
    this.rootPieceHeight = defaultRootPieceHeight;
    this.estimates.clear();
  }

  rootGapHeight(from: number, next: number, generation = this.generation): number {
    const height = generation === this.generation ? this.rootPieceHeight : defaultRootPieceHeight;
    return Math.max(0, next - from) * height;
  }

  gapHeight(
    kind: ShellKind,
    parent: number,
    from: number,
    next: number,
    generation = this.generation,
  ): number {
    const estimate = generation === this.generation ? this.estimates.get(parent) : undefined;
    const height = estimate?.kind === kind ? estimate.height : kind === "code" ? 24 : 40;
    return Math.max(0, next - from) * height;
  }

  seekTarget(
    root: HTMLElement,
    scrollport: HTMLElement,
    snapshot: MarkdownRenderSnapshot,
  ): MarkdownWindowSeekTarget | null {
    if (snapshot.generation !== this.generation || !this.matches(root)) return null;
    const viewport = this.visibleBounds(root, scrollport);
    if (!viewport) return null;
    const targetY = viewport.top + (viewport.bottom - viewport.top) / 2;
    const gap = [...root.querySelectorAll<HTMLElement>("[data-markdown-gap]")].find((element) => {
      const rect = element.getBoundingClientRect();
      return (
        rect.bottom > targetY &&
        rect.top <= targetY &&
        rect.right > viewport.left &&
        rect.left < viewport.right
      );
    });
    if (!gap) return null;

    const kind = gap.dataset.markdownGap;
    const from = Number(gap.dataset.gapFrom);
    const next = Number(gap.dataset.gapNext);
    const rect = gap.getBoundingClientRect();
    if (
      !Number.isSafeInteger(from) ||
      !Number.isSafeInteger(next) ||
      from < 0 ||
      next <= from ||
      rect.height <= 0
    )
      return null;
    const withinGap = Math.max(0, Math.min(1, (targetY - rect.top) / rect.height));
    const offset = targetY - scrollport.getBoundingClientRect().top;
    if (kind === "root" || kind === "ordinary")
      return {
        generation: snapshot.generation,
        index: Math.min(next - 1, from + Math.floor(withinGap * (next - from))),
        offset,
      };
    if (kind !== "code" && kind !== "table") return null;

    const shell = gap.closest<HTMLElement>(`[data-kind="${kind}"]`);
    const context = snapshot.contexts.get(Number(shell?.dataset.context))?.context;
    if (!shell || context?.kind !== kind || next > context.childCount) return null;
    const firstBodyChild =
      kind === "table"
        ? [...snapshot.contexts.values()].filter(
            ({ context: row }) => row.kind === "row" && row.parent === context.id && row.header,
          ).length
        : 0;
    if (from < firstBodyChild) return null;
    return {
      generation: snapshot.generation,
      contextId: context.id,
      childIndex: Math.min(next - 1, from + Math.floor(withinGap * (next - from))),
      offset,
    };
  }

  measure(root: HTMLElement, scrollport: HTMLElement = root): boolean {
    if (!this.matches(root)) return false;
    const viewport = this.visibleBounds(root, scrollport);
    if (!viewport) return false;
    const samples = new Map<number, { kind: ShellKind; total: number; count: number }>();
    const rootSamples: {
      element: HTMLElement;
      height: number;
      pieceCount: number;
      marginTop: number;
      marginBottom: number;
    }[] = [];
    for (const element of root.querySelectorAll<HTMLElement>(
      '[data-kind="document"] > [data-root-piece-count]',
    )) {
      const pieceCount = Number(element.dataset.rootPieceCount);
      const rect = element.getBoundingClientRect();
      if (
        !Number.isSafeInteger(pieceCount) ||
        pieceCount <= 0 ||
        rect.bottom <= viewport.top ||
        rect.top >= viewport.bottom ||
        rect.right <= viewport.left ||
        rect.left >= viewport.right ||
        rect.height <= 0 ||
        element.querySelectorAll("[data-piece]").length !== pieceCount
      )
        continue;
      const style = element.ownerDocument.defaultView?.getComputedStyle(element);
      const marginTop = Number.parseFloat(style?.marginTop ?? "") || 0;
      const marginBottom = Number.parseFloat(style?.marginBottom ?? "") || 0;
      rootSamples.push({ element, height: rect.height, pieceCount, marginTop, marginBottom });
    }
    let rootHeight = 0;
    let rootPieceCount = 0;
    for (let index = 0; index < rootSamples.length; index++) {
      const sample = rootSamples[index]!;
      const previous = rootSamples[index - 1];
      const next = rootSamples[index + 1];
      const previousMargin =
        previous?.element.nextElementSibling === sample.element
          ? Math.max(previous.marginBottom, sample.marginTop)
          : sample.marginTop;
      const nextMargin =
        next?.element === sample.element.nextElementSibling
          ? Math.max(sample.marginBottom, next.marginTop)
          : sample.marginBottom;
      rootHeight += sample.height + (previousMargin + nextMargin) / 2;
      rootPieceCount += sample.pieceCount;
    }
    for (const element of root.querySelectorAll<HTMLElement>(
      '[data-kind="codeLine"], tbody > [data-kind="row"]',
    )) {
      const rect = element.getBoundingClientRect();
      if (
        rect.bottom <= viewport.top ||
        rect.top >= viewport.bottom ||
        rect.right <= viewport.left ||
        rect.left >= viewport.right ||
        rect.height <= 0
      )
        continue;
      const shell = element.closest<HTMLElement>('[data-kind="code"], [data-kind="table"]');
      if (!shell) continue;
      const kind = shell.dataset.kind as ShellKind;
      const parent = Number(shell.dataset.context);
      const sample = samples.get(parent) ?? { kind, total: 0, count: 0 };
      sample.total += rect.height;
      sample.count++;
      samples.set(parent, sample);
    }
    let changed = false;
    if (rootPieceCount > 0) {
      const height = rootHeight / rootPieceCount;
      if (Math.abs(this.rootPieceHeight - height) >= 0.01) {
        this.rootPieceHeight = height;
        changed = true;
      }
    }
    // Keep one estimate per currently mounted shell, never a row/line history.
    const mounted = new Set(
      [...root.querySelectorAll<HTMLElement>('[data-kind="code"], [data-kind="table"]')].map(
        (shell) => Number(shell.dataset.context),
      ),
    );
    for (const parent of this.estimates.keys())
      if (!mounted.has(parent)) this.estimates.delete(parent);
    for (const [parent, sample] of samples) {
      const height = sample.total / sample.count;
      const prior = this.estimates.get(parent);
      if (prior?.kind === sample.kind && Math.abs(prior.height - height) < 0.01) continue;
      this.estimates.set(parent, { kind: sample.kind, height });
      changed = true;
    }
    return changed;
  }

  capture(root: HTMLElement, scrollport: HTMLElement = root): MarkdownWindowAnchor | null {
    if (!this.matches(root)) return null;
    const viewport = this.visibleBounds(root, scrollport);
    if (!viewport) return null;
    for (const element of root.querySelectorAll<HTMLElement>("[data-piece]")) {
      const rect = this.textRect(element);
      if (!rect || rect.bottom <= viewport.top || rect.top >= viewport.bottom) continue;
      if (rect.right <= viewport.left || rect.left >= viewport.right) continue;
      return {
        generation: this.generation,
        piece: element.dataset.piece!,
        start: element.dataset.sourceStart!,
        end: element.dataset.sourceEnd!,
        offset: rect.top - scrollport.getBoundingClientRect().top,
      };
    }
    return null;
  }

  restore(
    root: HTMLElement,
    anchor: MarkdownWindowAnchor | null,
    scrollport: HTMLElement = root,
  ): boolean {
    if (!anchor || anchor.generation !== this.generation || !this.matches(root)) return false;
    const viewport = this.visibleBounds(root, scrollport);
    if (!viewport) return false;
    // No selector interpolation: even caller-supplied anchor IDs remain inert.
    const element = [...root.querySelectorAll<HTMLElement>("[data-piece]")].find(
      (piece) =>
        piece.dataset.piece === anchor.piece &&
        piece.dataset.sourceStart === anchor.start &&
        piece.dataset.sourceEnd === anchor.end,
    );
    if (!element) return false;
    const rect = this.textRect(element);
    if (!rect) return false;
    const delta = rect.top - scrollport.getBoundingClientRect().top - anchor.offset;
    scrollport.scrollTop += delta;
    const restored = this.textRect(element);
    const restoredViewport = this.visibleBounds(root, scrollport);
    return (
      restored !== null &&
      restoredViewport !== null &&
      Math.abs(restored.top - scrollport.getBoundingClientRect().top - anchor.offset) <= 1
    );
  }

  private visibleBounds(
    root: HTMLElement,
    scrollport: HTMLElement,
  ): { top: number; right: number; bottom: number; left: number } | null {
    const rootBounds = root.getBoundingClientRect();
    const portBounds = scrollport.getBoundingClientRect();
    const bounds = {
      top: Math.max(rootBounds.top, portBounds.top),
      right: Math.min(rootBounds.right, portBounds.right),
      bottom: Math.min(rootBounds.bottom, portBounds.bottom),
      left: Math.max(rootBounds.left, portBounds.left),
    };
    return bounds.bottom > bounds.top && bounds.right > bounds.left ? bounds : null;
  }

  private matches(root: HTMLElement): boolean {
    return (
      root.dataset.markdownGeneration === this.generation &&
      Math.abs(root.getBoundingClientRect().width - this.width) <= 1
    );
  }

  private textRect(element: HTMLElement): DOMRect | null {
    if (!element.textContent || !element.dataset.sourceStart || !element.dataset.sourceEnd)
      return null;
    const range = element.ownerDocument.createRange();
    range.selectNodeContents(element);
    return range.getClientRects()[0] ?? null;
  }
}
