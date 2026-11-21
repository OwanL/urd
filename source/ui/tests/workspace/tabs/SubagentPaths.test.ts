import assert from "node:assert/strict";
import { test } from "node:test";
import { subagentPaths } from "../../../workspace/tabs/SubagentPaths.ts";

void test("subagent paths contain only descendants, in tree order, excluding the root", () => {
  const links = [
    { id: "grandchild", parentId: "child", title: "Grandchild" },
    { id: "child", parentId: "root", title: "Child" },
    { id: "root", parentId: "child", title: "Root" },
    { id: "other", parentId: "elsewhere", title: "Other" },
    { id: "second", parentId: "root", title: "Second" },
  ];
  assert.deepEqual(
    subagentPaths("root", links).map((path) => path.map((link) => link.id)),
    [["child"], ["child", "grandchild"], ["second"]],
  );
});
