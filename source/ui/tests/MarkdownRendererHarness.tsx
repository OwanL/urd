import { render } from "preact";
import { MarkdownRenderer } from "../markdown-rendering/MarkdownRenderer.tsx";
import { MarkdownSelectionPins } from "../markdown-rendering/MarkdownSelectionPins.ts";
import { MarkdownWindowAssembler } from "../markdown-rendering/MarkdownWindowAssembler.ts";
import { MarkdownWindowGeometry } from "../markdown-rendering/MarkdownWindowGeometry.ts";
import type { MarkdownWindowAnchor } from "../markdown-rendering/MarkdownWindowGeometry.ts";
import type {
  MarkdownPieceInterval,
  MarkdownRenderContext,
  MarkdownTextPiece,
  MarkdownValueKind,
} from "../markdown-rendering/MarkdownRenderModel.ts";
import {
  createMarkdownContext,
  createMarkdownPiece,
  createMarkdownValue,
  SyntheticMarkdownPageSource,
} from "./MarkdownRenderFixture.ts";

const contexts: {
  context: MarkdownRenderContext;
  values: ReturnType<typeof createMarkdownValue>[];
}[] = [];
const pieces: MarkdownTextPiece[] = [];
const lastChild = new Map<number, number>();
let byte = 0;
function node(
  kind: MarkdownRenderContext["kind"],
  parent: number,
  options: Partial<MarkdownRenderContext> = {},
  values: Partial<Record<MarkdownValueKind, string>> = {},
) {
  const id = contexts.length;
  const entries = Object.entries(values).map(([key, value]) =>
    createMarkdownValue(key as MarkdownValueKind, value),
  );
  contexts.push({
    context: createMarkdownContext(id, {
      kind,
      parent,
      firstPiece: pieces.length,
      source: { start: byte, end: byte },
      valueCount: entries.length,
      ...options,
    }),
    values: entries,
  });
  const previous = lastChild.get(parent);
  if (previous !== undefined)
    contexts[previous]!.context = { ...contexts[previous]!.context, nextSibling: id };
  if (parent >= 0) {
    const parentContext = contexts[parent]!.context;
    contexts[parent]!.context = {
      ...parentContext,
      firstChild: parentContext.firstChild < 0 ? id : parentContext.firstChild,
      childCount: parentContext.childCount + 1,
    };
  }
  lastChild.set(parent, id);
  return id;
}
function text(
  parent: number,
  value: string,
  kind: MarkdownTextPiece["kind"] = "text",
  continued = false,
  part = 0,
) {
  const start = byte;
  byte += new TextEncoder().encode(value).length;
  pieces.push(
    createMarkdownPiece(pieces.length, parent, value, {
      kind,
      source: { start, end: byte },
      mapping: kind === "literal" ? "containing" : "exact",
      continued,
      part,
    }),
  );
  for (let id = parent; id >= 0; id = contexts[id]!.context.parent) {
    const entry = contexts[id]!;
    entry.context = {
      ...entry.context,
      pieceCount: entry.context.pieceCount + 1,
      source: { ...entry.context.source, end: byte },
    };
  }
}
function leaf(
  kind: MarkdownRenderContext["kind"],
  parent: number,
  value: string,
  options: Partial<MarkdownRenderContext> = {},
) {
  const id = node(kind, parent, options);
  text(id, value);
  return id;
}
function link(parent: number, label: string, destination: string) {
  text(node("link", parent, {}, { destination, title: "Documentation" }), label);
}
const links = [
  "https://example.test/docs",
  "mailto:dev@example.test",
  "/guide?q=1#part",
  "#local",
  "javascript:alert(1)",
  "data:text/html,unsafe",
  "//example.test",
  "java\nscript:alert(1)",
];
const mode = new URLSearchParams(location.search).get("mode");
const documentId = node("document", -1);
let detachedFrom = 0;
function tableWithHeaders(labels: string[]) {
  const table = node("table", documentId);
  const header = node("row", table, { header: true });
  const headers = labels.map((label, position) =>
    leaf("cell", header, label, {
      position,
      header: true,
      headerCell: contexts.length,
      alignment: position === 2 ? "right" : "none",
    }),
  );
  return { table, headers };
}
if (mode === "long-code") {
  const code = node("code", documentId, {}, { language: "typescript" });
  for (let position = 0; position < 512; position++) {
    const line = node("codeLine", code, { position });
    text(
      line,
      `const entry_${String(position).padStart(4, "0")} = "${"long_identifier_".repeat(8)}";\n`,
      "code",
    );
  }
} else if (mode === "long-table") {
  const { table, headers } = tableWithHeaders(["Name", "Description", "Count", "Notes"]);
  for (let position = 0; position < 512; position++) {
    const row = node("row", table, { position: position + 1 });
    [
      `Row ${String(position).padStart(4, "0")}`,
      `Windowed row ${position} retains source-backed text and its table structure.`,
      String(position),
      "",
    ].forEach((value, cellPosition) =>
      leaf("cell", row, value, {
        position: cellPosition,
        headerCell: headers[cellPosition]!,
        alignment: cellPosition === 2 ? "right" : "none",
      }),
    );
  }
} else if (mode === "links") {
  links.forEach((destination, index) =>
    link(node("paragraph", documentId), `link-${index}`, destination),
  );
} else {
  if (mode === "selection") {
    for (let index = 0; index < 700; index++)
      leaf("paragraph", documentId, `Selection paragraph ${index}.`);
  } else if (mode !== "detached") {
    leaf("heading", documentId, "Renderer sample", { level: 2 });
    const paragraph = node("paragraph", documentId);
    text(paragraph, "Readable");
    text(paragraph, " UTF-8: café 🧭. ");
    leaf("emphasis", paragraph, "Emphasis");
    text(paragraph, ", ");
    leaf("strong", paragraph, "strong");
    text(paragraph, " and ");
    text(node("inlineCode", paragraph), "inline_code", "code");
    text(paragraph, ".");
    text(paragraph, "\n", "softBreak");
    text(paragraph, "Soft line, then a hard break.");
    text(paragraph, "\n", "hardBreak");
    text(paragraph, "Next line.");
    const tight = node("list", documentId);
    const first = node("item", tight);
    const firstParagraph = node("paragraph", first);
    text(firstParagraph, "First");
    text(firstParagraph, " tight item");
    const nested = node("list", first);
    leaf("paragraph", node("item", nested), "Nested first");
    leaf("paragraph", node("item", nested, { position: 1 }), "Nested second");
    leaf("paragraph", node("item", tight, { position: 1 }), "Second tight item");
    const loose = node("list", documentId, { ordered: true, loose: true }, { orderedStart: "7" });
    const looseItem = node("item", loose);
    leaf("paragraph", looseItem, "First loose item");
    leaf("paragraph", looseItem, "Another paragraph in the same item.");
    leaf("paragraph", node("item", loose, { position: 1 }), "Second loose item");
    leaf("paragraph", node("quote", documentId), "A quote retains its native structure.");
    const code = node("code", documentId, {}, { language: "typescript", fenceInfo: "inert" });
    const firstCodeLine = node("codeLine", code);
    text(firstCodeLine, "const", "code");
    text(firstCodeLine, " compass = '🧭';\n", "code");
    text(node("codeLine", code, { position: 1 }), `  ${"long_identifier_".repeat(16)}\n`, "code");
    text(node("thematicBreak", documentId), "", "thematicBreak");
  }
  if (mode !== "selection") {
    const { table, headers } = tableWithHeaders(["Name", "Description", "Count"]);
    detachedFrom = pieces.length;
    const row = node("row", table, { position: 1 });
    ["Middle-window row", "A detached header remains associated.", "42"].forEach(
      (value, position) =>
        leaf("cell", row, value, {
          position,
          headerCell: headers[position]!,
          alignment: position === 2 ? "right" : "none",
        }),
    );
    node("cell", row, { position: 3 });
    if (mode !== "detached") {
      leaf("paragraph", documentId, "Literal content (never parsed or fetched):");
      text(
        node("paragraph", documentId),
        '<img src="https://resource.invalid/pixel.png" onerror="alert(1)">\n<script>alert(1)</script>\n![deferred image](https://resource.invalid/image.png)',
        "literal",
      );
      const paragraph = node("paragraph", documentId);
      link(paragraph, "Safe link", "https://example.test/docs");
      text(paragraph, " · ");
      link(paragraph, "Unsafe label", "javascript:alert(1)");
      const continued = node("paragraph", documentId);
      text(continued, "Stable", "text", true);
      text(continued, " fragment 🧭 — ", "text", true, 1);
      text(continued, "later page ", "text", true, 2);
      text(continued, "appended.", "text", false, 3);
    }
  }
}
const source = new SyntheticMarkdownPageSource(pieces, contexts, {
  piecePageSize:
    mode === "long-code"
      ? 24
      : mode === "long-table"
        ? 104
        : mode === "selection" || mode === "detached" || mode === "links"
          ? 1000
          : pieces.length - 2,
});
const assembler = new MarkdownWindowAssembler(
  source,
  source.manifest,
  mode === "detached" ? detachedFrom : 0,
);
const root = document.querySelector("#markdown-renderer")!;
const longWindow = mode === "long-code" || mode === "long-table";
root.toggleAttribute("data-windowed", longWindow);
const geometry = longWindow ? new MarkdownWindowGeometry() : null;
const geometryProfile = mode ?? "default";
function owner() {
  return root.querySelector<HTMLElement>(".markdown-renderer")!;
}
function paint() {
  render(<MarkdownRenderer snapshot={assembler.snapshot} geometry={geometry ?? undefined} />, root);
}
function paintMeasured(anchor: MarkdownWindowAnchor | null) {
  paint();
  if (!geometry) return;
  if (geometry.measure(owner())) paint();
  geometry.restore(owner(), anchor);
}
await assembler.append(new AbortController().signal);
if (geometry) geometry.configure(assembler.snapshot.generation, root.clientWidth, geometryProfile);
paintMeasured(null);
let selectionChanges = 0;
const rendererRoot = root.querySelector<HTMLElement>(".markdown-renderer")!;
let selectionPins: MarkdownSelectionPins | null = null;
if (mode === "selection" || longWindow)
  selectionPins = new MarkdownSelectionPins(
    rendererRoot,
    () => assembler.snapshot,
    () => {
      selectionChanges++;
      selectionPins?.read();
    },
  );
let pendingGeometryAnchor: MarkdownWindowAnchor | null = null;
async function moveWindow(direction: "forward" | "backward") {
  if (!geometry) throw new Error("A long-window mode is required.");
  const anchor = geometry.capture(owner());
  if (direction === "forward") await assembler.append(new AbortController().signal);
  else await assembler.prepend(new AbortController().signal);
  const snapshot = assembler.snapshot;
  const pieceSize = mode === "long-code" ? 48 : 48 * 4;
  const minimum = mode === "long-code" ? 0 : 4;
  const active =
    direction === "forward"
      ? { from: Math.max(minimum, snapshot.next - pieceSize), next: snapshot.next }
      : { from: snapshot.from, next: Math.min(snapshot.next, snapshot.from + pieceSize) };
  await assembler.trim(
    {
      generation: snapshot.generation,
      active,
      retain: (selectionPins?.read() ?? []).map(({ from, next }) => {
        const unit = mode === "long-code" ? 1 : 4;
        return { from: Math.floor(from / unit) * unit, next: Math.ceil(next / unit) * unit };
      }),
    },
    new AbortController().signal,
  );
  paintMeasured(anchor);
}
const harness = {
  get pageSize() {
    return mode === "long-code" ? 24 : mode === "long-table" ? 104 : 0;
  },
  get pieceCount() {
    return source.manifest.pieceCount;
  },
  get pageCursors() {
    return [
      ...source.pieceRequests.map(({ index }) => index),
      ...source.beforePieceRequests.map(({ index }) => index),
    ];
  },
  moveWindow,
  captureAnchor() {
    if (!geometry) throw new Error("A long-window mode is required.");
    pendingGeometryAnchor = geometry.capture(owner());
  },
  reconfigureGeometry() {
    if (!geometry) throw new Error("A long-window mode is required.");
    geometry.configure(assembler.snapshot.generation, root.clientWidth, geometryProfile);
    if (geometry.measure(owner())) paint();
    geometry.restore(owner(), pendingGeometryAnchor);
    pendingGeometryAnchor = null;
  },
  links,
  get selectionChanges() {
    return selectionChanges;
  },
  readSelectionPins() {
    return selectionPins?.read() ?? [];
  },
  trim(active: MarkdownPieceInterval) {
    if (!selectionPins) throw new Error("Selection mode is required.");
    const snapshot = assembler.snapshot;
    const retain = selectionPins.read();
    return assembler
      .trim({ generation: snapshot.generation, active, retain }, new AbortController().signal)
      .then(paint);
  },
  disposeSelectionPins() {
    selectionPins?.dispose();
  },
  get snapshot() {
    return assembler.snapshot;
  },
  append() {
    return assembler.append(new AbortController().signal).then(paint);
  },
  unmountRemount() {
    render(null, root);
    paint();
  },
  duplicate() {
    const snapshot = { ...assembler.snapshot, generation: "replacement generation" };
    render(
      <>
        <MarkdownRenderer snapshot={snapshot} />
        <MarkdownRenderer snapshot={snapshot} />
      </>,
      root,
    );
  },
};
export type MarkdownRendererHarness = typeof harness;
Object.assign(window, { markdownRendererHarness: harness });
document.documentElement.dataset.ready = "true";
