import assert from "node:assert/strict";
import test from "node:test";
import { formatToolInput, toolCallClipboardText } from "../chat/ToolCallInspection.ts";

void test("pretty-prints JSON input and preserves non-JSON input", () => {
  assert.equal(
    formatToolInput('{"path":"src/a.ts","line":2}'),
    '{\n  "path": "src/a.ts",\n  "line": 2\n}',
  );
  assert.equal(formatToolInput("not JSON"), "not JSON");
});

void test("composes clearly labelled clipboard text and represents running output", () => {
  assert.equal(
    toolCallClipboardText({ input: '{"ok":true}', output: "done", truncated: false }),
    'Input\n{\n  "ok": true\n}\n\nOutput\ndone',
  );
  assert.equal(
    toolCallClipboardText({ input: "{}", output: null, truncated: false }),
    "Input\n{}\n\nOutput\nRunning…",
  );
});
