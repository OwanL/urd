import assert from "node:assert/strict";
import test from "node:test";
import { parseSubagentDepthInput } from "../../preferences/SubagentDepthInput.ts";

void test("subagent depth accepts nonnegative 32-bit integers", () => {
  assert.equal(parseSubagentDepthInput("0"), 0);
  assert.equal(parseSubagentDepthInput("4"), 4);
  assert.equal(parseSubagentDepthInput("2147483647"), 2_147_483_647);
  for (const value of ["", "-1", "1.5", "1e2", " 4", "2147483648", "99999999999999999999"])
    assert.equal(parseSubagentDepthInput(value), null, value);
});
