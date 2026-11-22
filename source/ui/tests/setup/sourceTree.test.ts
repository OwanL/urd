import assert from "node:assert/strict";
import test from "node:test";
import {
  buildSourceTree,
  filenameKey,
  isWithin,
  pathKey,
  setSourceIgnored,
  sourceSelection,
} from "../../setup/sourceTree.ts";

void test("groups sources and counts descendants without prefix collisions", () => {
  const roots = buildSourceTree(
    ["/repo/a/AGENTS.md", "/repo/a/nested/AGENTS.md", "/repo/ab/AGENTS.md", "/packaged/SKILL.md"],
    "/repo",
  );
  assert.equal(roots[0]?.count, 3);
  assert.equal(roots[0]?.children[0]?.count, 2);
  assert.equal(roots[1]?.count, 1);
  assert.equal(isWithin("/repo/ab/file", "/repo/a"), false);
});

void test("recognizes Windows separators and case for inheritance", () => {
  const roots = buildSourceTree(
    ["C:\\Repo\\src\\AGENTS.md", "C:\\Repo\\src\\CLAUDE.md"],
    "c:\\repo",
  );
  assert.equal(roots[0]?.children[0]?.count, 2);
  assert.equal(isWithin("C:\\REPO\\src\\AGENTS.md", "c:/repo/SRC"), true);
});

void test("filename groups ignore case and hyphen aliases without changing path identity", () => {
  assert.equal(filenameKey("agent-instructions.md"), filenameKey("AGENT_INSTRUCTIONS.md"));
  assert.notEqual(pathKey("/repo/agent-instructions.md"), pathKey("/repo/AGENT_INSTRUCTIONS.md"));
});

void test("filesystem roots include descendants and retain absolute child paths", () => {
  assert.equal(isWithin("/repo/AGENTS.md", "/"), true);
  assert.equal(isWithin("C:\\repo\\AGENTS.md", "c:\\"), true);
  assert.equal(isWithin("\\\\server\\share\\dir\\AGENTS.md", "\\\\SERVER\\SHARE\\"), true);
  const posix = buildSourceTree(["/repo/AGENTS.md"], "/");
  assert.equal(posix[0]?.path, "/");
  assert.equal(posix[0]?.children[0]?.path, "/repo");
  const drive = buildSourceTree(["C:\\repo\\AGENTS.md"], "C:\\");
  assert.equal(drive[0]?.children[0]?.path, "C:/repo");
  const unc = buildSourceTree(["\\\\server\\share\\dir\\AGENTS.md"], "\\\\server\\share\\");
  assert.equal(unc[0]?.children[0]?.path, "\\\\server\\share/dir");
});

void test("refines an inherited subtree and reports partial parents", () => {
  const tree = buildSourceTree(["/repo/a/one.md", "/repo/a/two.md", "/repo/b/three.md"], "/repo");
  const root = tree[0]!;
  const a = root.children[0]!;
  const one = a.children[0]!;
  const rules = setSourceIgnored({ paths: ["/repo", "/elsewhere"], includes: [] }, one, false);
  assert.deepEqual(rules, { paths: ["/repo", "/elsewhere"], includes: ["/repo/a/one.md"] });
  assert.equal(sourceSelection(root, rules, []), "partial");
  assert.equal(sourceSelection(a, rules, []), "partial");
  assert.equal(sourceSelection(one, rules, []), "none");
  assert.deepEqual(setSourceIgnored(rules, a, true), {
    paths: ["/repo", "/elsewhere"],
    includes: [],
  });
  assert.deepEqual(setSourceIgnored(rules, a, false), {
    paths: ["/repo", "/elsewhere"],
    includes: ["/repo/a"],
  });
});

void test("filename rules contribute to selection without changing location rules", () => {
  const tree = buildSourceTree(["/repo/AGENTS.md", "/repo/CLAUDE.md"], "/repo");
  const rules = { paths: ["/repo"], includes: ["/repo/CLAUDE.md"] };
  assert.equal(sourceSelection(tree[0]!, rules, ["CLAUDE.md"]), "partial");
  assert.equal(sourceSelection(tree[0]!.children[1]!, rules, ["CLAUDE.md"]), "all");
});

void test("nested and missing exceptions affect parents and survive unrelated changes", () => {
  const root = buildSourceTree(["/repo/a/one.md", "/repo/b/two.md"], "/repo")[0]!;
  const rules = { paths: ["/repo", "/repo/missing/deeper"], includes: ["/repo/missing"] };
  assert.equal(sourceSelection(root, rules, []), "partial");
  assert.equal(sourceSelection(root.children[0]!, rules, []), "all");
  const missing = { name: "missing", path: "/repo/missing", children: [], count: 0, file: false };
  assert.equal(sourceSelection(missing, rules, []), "partial");
  assert.deepEqual(setSourceIgnored(rules, missing, false), {
    paths: ["/repo"],
    includes: ["/repo/missing"],
  });
  assert.deepEqual(setSourceIgnored(rules, root, false), { paths: [], includes: [] });
});

void test("external roots retain their identity rather than collapsing into relative paths", () => {
  const roots = buildSourceTree(
    ["/outside/AGENTS.md", "outside/CLAUDE.md", "\\\\host\\share\\AGENTS.md"],
    "/repo",
  );
  assert.deepEqual(
    roots.map((root) => root.path),
    ["/outside", "outside", "//host"],
  );
  assert.equal(roots[0]?.count, 1);
  assert.equal(roots[1]?.count, 1);
  assert.equal(isWithin("/other/AGENTS.md", "/repo"), false);
});
