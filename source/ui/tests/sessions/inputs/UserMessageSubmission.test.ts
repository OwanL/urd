import assert from "node:assert/strict";
import test from "node:test";
import { submissionTextError } from "../../../sessions/inputs/UserMessageSubmission.ts";

void test("text validation accepts large submissions and rejects incomplete Unicode without trimming", () => {
  assert.equal(submissionTextError("  \r\n雪 🦉  "), null);
  assert.equal(submissionTextError("🦉".repeat(4096)), null);
  assert.equal(submissionTextError("🦉".repeat(4096) + " "), null);
  for (const text of ["\ud800", "\udfff", "x\ud800y", "\udfff🦉"])
    assert.match(submissionTextError(text) ?? "", /Unicode/);
});
