import { strict as assert } from "node:assert";
import { test } from "node:test";
import {
  subagentReportPreview,
  subagentReportText,
} from "../../../../chat/transcript/tool-calls/SubagentReportText.ts";

void test("removes only the matching returned background header", () => {
  for (const status of ["", " (failed)", " (cancelled)"]) {
    const output = `Results from background subagent call, ID: call-1${status}\nfirst\nsecond\n`;
    assert.equal(subagentReportText(output, true, "call-1"), "first\nsecond\n");
    assert.equal(subagentReportText(output, false, "call-1"), output);
    assert.equal(subagentReportText(output, true, "call-2"), output);
  }
  const partial = "Results from background subagent call, ID: call-1";
  assert.equal(subagentReportText(partial, true, "call-1"), partial);
  assert.equal(subagentReportText("plain error\n", true, "call-1"), "plain error\n");
});

void test("limits previews without changing short reports", () => {
  assert.equal(subagentReportPreview("first\nsecond", 5), "first");
  assert.equal(subagentReportPreview("first\n", 10), "first\n");
  assert.equal(subagentReportPreview("A😊", 2), "A");
  assert.equal(subagentReportPreview("1\n2\n3\n4\n5\n6\n7", 100), "1\n2\n3\n4\n5\n6");
});
