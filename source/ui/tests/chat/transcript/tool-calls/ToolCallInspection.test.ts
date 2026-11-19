import assert from "node:assert/strict";
import test from "node:test";
import {
  formatToolInput,
  reusableToolCallPreview,
  toolCallClipboardText,
} from "../../../../chat/transcript/tool-calls/ToolCallInspection.ts";

void test("pretty-prints JSON input and preserves non-JSON input", () => {
  assert.equal(
    formatToolInput('{"path":"src/a.ts","line":2}'),
    '{\n  "path": "src/a.ts",\n  "line": 2\n}',
  );
  assert.equal(formatToolInput("not JSON"), "not JSON");
});

void test("reuses only a complete preview for the active finished tool at the current revision", () => {
  const target = {
    id: 7,
    requestId: "request",
    callId: "call",
    historyRevision: 12,
    payloadFinished: true,
  };
  const transcript = {
    input: "{}",
    output: "done",
    truncated: false,
    projectionFinal: true,
    inputTokens: 1,
    outputTokens: 1,
  };
  const preview = { ...target, transcript };

  assert.equal(reusableToolCallPreview(preview, target, 12), transcript);
});

void test("requires a full read for incomplete, stale, unsettled or mismatched previews", () => {
  const target = {
    id: 7,
    requestId: "request",
    callId: "call",
    historyRevision: 12,
    payloadFinished: true,
  };
  const transcript = {
    input: "{}",
    output: "done",
    truncated: false,
    projectionFinal: true,
    inputTokens: 1,
    outputTokens: 1,
  };
  const preview = { ...target, transcript };

  assert.equal(reusableToolCallPreview(null, target, 12), null);
  assert.equal(
    reusableToolCallPreview(
      { ...preview, transcript: { ...transcript, projectionFinal: false } },
      target,
      12,
    ),
    null,
  );
  assert.equal(
    reusableToolCallPreview(
      { ...preview, transcript: { ...transcript, truncated: true } },
      target,
      12,
    ),
    null,
  );
  assert.equal(
    reusableToolCallPreview(
      { ...preview, transcript: { ...transcript, output: null } },
      target,
      12,
    ),
    null,
  );
  assert.equal(reusableToolCallPreview(preview, { ...target, payloadFinished: false }, 12), null);
  assert.equal(reusableToolCallPreview({ ...preview, payloadFinished: false }, target, 12), null);
  assert.equal(reusableToolCallPreview(preview, target, 13), null);
  assert.equal(reusableToolCallPreview(preview, { ...target, id: 8 }, 12), null);
  assert.equal(reusableToolCallPreview({ ...preview, requestId: "other" }, target, 12), null);
  assert.equal(reusableToolCallPreview({ ...preview, callId: "other" }, target, 12), null);
  assert.equal(reusableToolCallPreview({ ...preview, historyRevision: 11 }, target, 12), null);
});

void test("composes clearly labelled clipboard text and represents running output", () => {
  assert.equal(
    toolCallClipboardText({
      input: '{"ok":true}',
      output: "done",
      truncated: false,
      projectionFinal: true,
      inputTokens: 4,
      outputTokens: 1,
    }),
    'Input\n{\n  "ok": true\n}\n\nOutput\ndone',
  );
  assert.equal(
    toolCallClipboardText({
      input: "{}",
      output: null,
      truncated: false,
      projectionFinal: false,
      inputTokens: 1,
      outputTokens: null,
    }),
    "Input\n{}\n\nOutput\nRunning…",
  );
});
