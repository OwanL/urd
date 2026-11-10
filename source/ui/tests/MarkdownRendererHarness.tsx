import { render } from "preact";
import { MarkdownRenderer } from "../markdown-rendering/MarkdownRenderer.tsx";
import { MarkdownWindowAssembler } from "../markdown-rendering/MarkdownWindowAssembler.ts";
import type {
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
  else if (parent >= 0)
    contexts[parent]!.context = { ...contexts[parent]!.context, firstChild: id };
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
if (mode === "links") {
  links.forEach((destination, index) =>
    link(node("paragraph", documentId), `link-${index}`, destination),
  );
} else {
  if (mode !== "detached") {
    leaf("heading", documentId, "Renderer sample", { level: 2 });
    const paragraph = node("paragraph", documentId);
    text(paragraph, "Readable UTF-8: café 🧭. ");
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
    leaf("paragraph", first, "First tight item");
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
    text(node("codeLine", code), "const compass = '🧭';\n", "code");
    text(node("codeLine", code, { position: 1 }), `  ${"long_identifier_".repeat(16)}\n`, "code");
    text(node("thematicBreak", documentId), "", "thematicBreak");
  }
  const table = node("table", documentId);
  const header = node("row", table, { header: true });
  const headers = ["Name", "Description", "Count"].map((label, position) =>
    leaf("cell", header, label, {
      position,
      header: true,
      headerCell: contexts.length,
      alignment: position === 2 ? "right" : "none",
    }),
  );
  detachedFrom = pieces.length;
  const row = node("row", table, { position: 8 });
  ["Middle-window row", "A detached header remains associated.", "42"].forEach((value, position) =>
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
    text(continued, "Stable fragment 🧭 — ", "text", true);
    text(continued, "later page appended.", "text", false, 1);
  }
}
const source = new SyntheticMarkdownPageSource(pieces, contexts, {
  piecePageSize: mode === "detached" || mode === "links" ? 1000 : pieces.length - 1,
});
const assembler = new MarkdownWindowAssembler(
  source,
  "fixture-generation",
  mode === "detached" ? detachedFrom : 0,
);
const root = document.querySelector("#markdown-renderer")!;
function paint() {
  render(<MarkdownRenderer snapshot={assembler.snapshot} />, root);
}
await assembler.append(new AbortController().signal);
paint();
Object.assign(window, {
  markdownRendererHarness: {
    links,
    get snapshot() {
      return assembler.snapshot;
    },
    async append() {
      await assembler.append(new AbortController().signal);
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
  },
});
document.documentElement.dataset.ready = "true";
