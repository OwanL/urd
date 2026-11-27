import assert from "node:assert/strict";
import test from "node:test";
import type { ToolDraft } from "../../../../sessions/SessionEvents.ts";
import { presentToolDraft } from "../../../../chat/transcript/tool-calls/ToolDraftPresentation.ts";

function draft(name: string, head: string, tail = ""): ToolDraft {
  return {
    session: "s",
    boot: "b",
    version: 1,
    executionId: "e",
    requestId: "r",
    callId: "c",
    entryId: "i",
    name,
    order: 0,
    state: "drafting",
    argumentBytes: head.length + tail.length,
    head,
    tail,
  };
}

void test("extracts a partial write without completing an escape", () => {
  const result = presentToolDraft(
    draft("write", String.raw`{"path":"C:\\src\\file.ts","content":"first\nsecond\u00`),
  );
  assert.equal(result.icon, "write");
  assert.equal(result.title, "file.ts");
  assert.deepEqual(result.preview, { style: "code", text: "first\nsecond" });
});

void test("reuses presentation for an unchanged event, not a replacement of the same version", () => {
  const event = draft("write", '{"content":"first');
  const presentation = presentToolDraft(event);
  assert.equal(presentToolDraft(event), presentation);
  assert.deepEqual(presentToolDraft({ ...event, head: '{"content":"second' }).preview, {
    style: "code",
    text: "second",
  });
});

void test("formats edit text and counts lines", () => {
  const result = presentToolDraft(
    draft(
      "edit",
      JSON.stringify({
        path: "src/a.ts",
        edits: [
          { oldText: "one\ntwo", newText: "three" },
          { oldText: "x", newText: "y\nz" },
        ],
      }),
    ),
  );
  assert.equal(result.title, "a.ts");
  assert.equal(result.detail, "−3 +3");
  assert.deepEqual(result.preview, {
    style: "edit",
    parts: [
      { kind: "removed", text: "− one\n− two" },
      { kind: "added", text: "+ three" },
      { kind: "removed", text: "− x" },
      { kind: "added", text: "+ y\n+ z" },
    ],
  });
});

void test("does not repeat command or prose in the header as arguments stream", () => {
  for (const [name, field, text] of [
    ["bash", "command", "echo hello"],
    ["shell", "command", "pwd"],
    ["subagent", "task", "Investigate this"],
    ["ask_user", "question", "Which one?"],
  ] as const) {
    for (let length = 1; length <= text.length; length++) {
      const result = presentToolDraft(draft(name, `{"${field}":"${text.slice(0, length)}`));
      assert.equal(result.detail, null, `${name} at ${length}`);
      assert.deepEqual(result.preview, {
        style: name === "bash" || name === "shell" ? "code" : "prose",
        text: text.slice(0, length),
      });
    }
  }
});

void test("extracts command and prose from incomplete arguments", () => {
  assert.deepEqual(presentToolDraft(draft("bash", '{"command":"echo hello')).preview, {
    style: "code",
    text: "echo hello",
  });
  assert.equal(presentToolDraft(draft("shell", '{"command":"pwd"}')).icon, "terminal");
  assert.deepEqual(presentToolDraft(draft("subagent", '{"task":"Investigate')).preview, {
    style: "prose",
    text: "Investigate",
  });
  assert.deepEqual(presentToolDraft(draft("ask_user", '{"question":"Which one?')).preview, {
    style: "prose",
    text: "Which one?",
  });
});

void test("attributes an edit tail before a newText marker to oldText", () => {
  for (const [key, kind, marker, detail] of [
    ["oldText", "removed", "−", "−4 +1"],
    ["newText", "added", "+", "−2 +3"],
  ]) {
    const result = presentToolDraft(
      draft(
        "edit",
        String.raw`{"path":"src/a.ts","${key}":"first\npart`,
        String.raw`last\nline"},{"newText":"replacement"}]}`,
      ),
    );
    assert.deepEqual(result.preview, {
      style: "edit",
      parts: [
        { kind, text: `${marker} first\n${marker} part` },
        { kind: "neutral", text: "…" },
        { kind: "removed", text: "− last\n− line" },
        { kind: "added", text: "+ replacement" },
      ],
    });
    assert.equal(result.detail, detail);
  }
});

void test("uses the current field for a marker-less edit tail", () => {
  const event = draft("edit", String.raw`{"path":"a","newText":"first`, String.raw`last\nline`);
  const result = presentToolDraft({ ...event, field: "newText" });
  assert.equal(result.detail, "−0 +3");
  assert.deepEqual(result.preview, {
    style: "edit",
    parts: [
      { kind: "added", text: "+ first" },
      { kind: "neutral", text: "…" },
      { kind: "added", text: "+ last\n+ line" },
    ],
  });
});

void test("decodes complete escapes in generic fragments without completing split escapes", () => {
  const result = presentToolDraft(
    draft("other", String.raw`{"value":"hello\nworld\u00`, String.raw`tail\tend`),
  );
  assert.deepEqual(result.preview, { style: "code", text: '{"value":"hello\nworld\n…\ntail\tend' });
});

void test("bounds generic fragments and does not parse a disconnected tail", () => {
  const result = presentToolDraft(
    draft("other", "x".repeat(5000), '{"path":"wrong"}' + "z".repeat(4000)),
  );
  assert.equal(result.title, "other");
  assert.equal(result.preview?.style === "code" && result.preview.text.length, 4096 + 3 + 3072);
  assert.equal(presentToolDraft(draft("read", '{"path":"src/', 'wrong.ts"}')).title, "src");
});
