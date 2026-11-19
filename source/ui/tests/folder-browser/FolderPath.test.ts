import assert from "node:assert/strict";
import test from "node:test";
import { folderBreadcrumbs } from "../../folder-browser/FolderPath.ts";

const cases = [
  {
    path: "C:\\dev\\repos\\urd",
    breadcrumbs: [
      { name: "C:\\", path: "C:\\" },
      { name: "dev", path: "C:\\dev" },
      { name: "repos", path: "C:\\dev\\repos" },
      { name: "urd", path: "C:\\dev\\repos\\urd" },
    ],
  },
  {
    path: "C:\\\\",
    breadcrumbs: [{ name: "C:\\", path: "C:\\" }],
  },
  {
    path: "/dev/repos",
    breadcrumbs: [
      { name: "/", path: "/" },
      { name: "dev", path: "/dev" },
      { name: "repos", path: "/dev/repos" },
    ],
  },
  {
    path: "/",
    breadcrumbs: [{ name: "/", path: "/" }],
  },
  {
    path: "\\\\server\\share\\My Folder\\docs\\",
    breadcrumbs: [
      { name: "\\\\server\\share", path: "\\\\server\\share\\" },
      { name: "My Folder", path: "\\\\server\\share\\My Folder" },
      { name: "docs", path: "\\\\server\\share\\My Folder\\docs" },
    ],
  },
] as const;

void test("builds canonical breadcrumbs for absolute folder paths", () => {
  for (const { path, breadcrumbs } of cases)
    assert.deepEqual(folderBreadcrumbs(path), breadcrumbs, path);
});
