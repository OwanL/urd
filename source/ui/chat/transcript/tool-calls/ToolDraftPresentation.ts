import type { ToolDraft } from "../../../sessions/SessionEvents.ts";
import type { ToolCallLabel } from "../../../sessions/transcript/Messages.ts";

interface EditPreviewPart {
  kind: "added" | "removed" | "neutral";
  text: string;
}

export type DraftPreview =
  | { style: "code" | "prose"; text: string }
  | { style: "edit"; parts: EditPreviewPart[] };

interface DraftPresentation {
  icon: ToolCallLabel["icon"];
  title: string;
  detail: string | null;
  preview: DraftPreview | null;
}

// Draft arguments are incomplete and may have a gap between head and tail. Never
// parse them as executable arguments or join the fragments into synthetic JSON.
interface StringField {
  key: string;
  value: string;
  complete: boolean;
}

const simpleEscapes: Record<string, string> = {
  '"': '"',
  "\\": "\\",
  "/": "/",
  b: "\b",
  f: "\f",
  n: "\n",
  r: "\r",
  t: "\t",
};

// Field parsing stops at invalid escapes; display fragments keep them literal.
// Neither mode displays a truncated escape.
function readJsonText(source: string, index: number, strict: boolean, stopAtQuote: boolean) {
  let text = "";
  while (index < source.length) {
    const char = source.charAt(index++);
    if (stopAtQuote && char === '"') return { text, end: index, complete: true };
    if (char !== "\\") {
      if (strict && char < " ") break;
      text += char;
      continue;
    }
    if (index >= source.length) break;
    const escape = source.charAt(index++);
    const simple = simpleEscapes[escape];
    if (simple !== undefined) {
      text += simple;
      continue;
    }
    if (escape === "u") {
      const digits = source.slice(index, index + 4);
      if (digits.length < 4) break;
      if (/^[0-9a-fA-F]{4}$/.test(digits)) {
        text += String.fromCharCode(Number.parseInt(digits, 16));
        index += 4;
        continue;
      }
    }
    if (strict) break;
    text += `\\${escape}`;
  }
  return { text, end: index, complete: false };
}

function strings(source: string): StringField[] {
  const fields: StringField[] = [];
  let index = 0;
  function readString() {
    const result = readJsonText(source, index + 1, true, true);
    index = result.end;
    return result;
  }

  while (index < source.length) {
    if (source[index] !== '"') {
      index++;
      continue;
    }
    const key = readString();
    if (!key.complete) break;
    while (/\s/.test(source[index] ?? "") && index < source.length) index++;
    if (source[index] !== ":") continue;
    index++;
    while (/\s/.test(source[index] ?? "") && index < source.length) index++;
    if (source[index] !== '"') continue;
    const value = readString();
    fields.push({ key: key.text, value: value.text, complete: value.complete });
    if (!value.complete) break;
  }
  return fields;
}

function unescapeText(source: string, stopAtQuote = false) {
  return readJsonText(source, 0, false, stopAtQuote);
}

function editPreview(fields: StringField[], tail: string, field: string | null | undefined) {
  const parts: EditPreviewPart[] = [];
  let added = 0;
  let removed = 0;
  let hasEdit = false;

  function appendEdits(fields: StringField[]) {
    for (const { key, value } of fields) {
      if (key !== "oldText" && key !== "newText") continue;
      hasEdit = true;
      if (!value) continue;
      const lines = value.split("\n");
      if (key === "oldText") {
        removed += lines.length;
        parts.push({ kind: "removed", text: lines.map((line) => `− ${line}`).join("\n") });
      } else {
        added += lines.length;
        parts.push({ kind: "added", text: lines.map((line) => `+ ${line}`).join("\n") });
      }
    }
  }

  appendEdits(fields);
  if (tail) {
    // A key inside a JSON string would have escaped closing quotes.
    const marker = /"(oldText|newText)"\s*:\s*"/.exec(tail);
    const start = marker?.index ?? -1;
    const prefix = start < 0 ? tail : tail.slice(0, start);
    const { text } = unescapeText(prefix, true);
    const prefixField = marker ? (marker[1] === "newText" ? "oldText" : "newText") : field;
    parts.push({ kind: "neutral", text: "…" });
    if (text && (prefixField === "oldText" || prefixField === "newText"))
      appendEdits([{ key: prefixField, value: text, complete: false }]);
    else if (text) parts.push({ kind: "neutral", text });
    if (start >= 0) appendEdits(strings(tail.slice(start)));
  }
  return { parts, detail: hasEdit ? `−${removed} +${added}` : null };
}

function oneLine(value: string, limit: number): string {
  const text = value.replace(/\s+/g, " ").trim();
  return text.length > limit ? `${text.slice(0, limit - 1)}…` : text;
}

// An accepted update replaces only its call's event object. Equal versions can
// still carry a promotion, so cache the event identity rather than its version number alone.
const presentations = new WeakMap<ToolDraft, DraftPresentation>();

export function presentToolDraft(draft: ToolDraft): DraftPresentation {
  const cached = presentations.get(draft);
  if (cached) return cached;
  const presentation = parseToolDraft(draft);
  presentations.set(draft, presentation);
  return presentation;
}

function parseToolDraft(draft: ToolDraft): DraftPresentation {
  const head = draft.head.slice(0, 4096);
  const tail = draft.tail.slice(-3072);
  const fields = strings(head);
  const last = fields.at(-1);
  const name = draft.name || "tool";
  if (tail && name !== "edit") {
    // The tail starts at an arbitrary byte, possibly inside an escape. Do not
    // join it to the head or interpret it as a continuation of that escape.
    if (last && !last.complete) {
      const suffix = unescapeText(tail, true);
      if (["content", "command", "task", "question"].includes(last.key))
        last.value += `\n…\n${suffix.text}`;
      fields.push(...strings(tail.slice(suffix.end)));
    } else {
      fields.push(...strings(tail));
    }
  }
  const get = (key: string) => fields.find((field) => field.key === key)?.value;
  const path = get("path");
  const title = path
    ? path
        .replace(/[\\/]+$/, "")
        .split(/[\\/]/)
        .pop() || name
    : name;
  const result: DraftPresentation = {
    icon: "generic",
    title: oneLine(name, 120),
    detail: null,
    preview: null,
  };

  if (name === "read" || name === "write" || name === "edit") {
    result.icon = name;
    result.title = oneLine(title, 120);
    if (name === "write") {
      const text = get("content");
      if (text) result.preview = { style: "code", text };
    } else if (name === "edit") {
      const { parts, detail } = editPreview(fields, tail, draft.field);
      result.detail = detail;
      if (parts.length) result.preview = { style: "edit", parts };
    }
  } else if (name === "bash" || name === "shell") {
    result.icon = "terminal";
    const text = get("command");
    if (text && draft.state === "drafting") result.preview = { style: "code", text };
    else if (text) result.detail = oneLine(text, 240);
  } else if (name === "subagent" || name === "ask_user") {
    const text = get(name === "subagent" ? "task" : "question");
    if (text) result.preview = { style: "prose", text };
  } else {
    const text = unescapeText(head).text + (tail ? `\n…\n${unescapeText(tail).text}` : "");
    if (text) result.preview = { style: "code", text };
  }
  return result;
}
