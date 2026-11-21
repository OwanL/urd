import assert from "node:assert/strict";
import test from "node:test";
import { buildSourceTree, filenameKey, isWithin, pathKey } from "../../setup/sourceTree.ts";

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
