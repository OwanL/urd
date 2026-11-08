import { execFileSync } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { resolve } from "node:path";

// Count physical source lines, including blanks and test helpers, not generated outputs.
const root = resolve(import.meta.dirname, "../..");
const paths = execFileSync("git", [
  "ls-files", "--cached", "--others", "--exclude-standard", "-z", "--", "source/",
], { cwd: root, encoding: "utf8" }).split("\0");
const totals = { application: 0, tests: 0 };
for (const path of new Set(paths)) {
  if (!/\.(cs|[cm]?js|jsx|ts|tsx|css|html)$/.test(path)) continue;
  const absolute = resolve(root, path);
  if (!existsSync(absolute)) continue; // Pending tracked deletions are not source.
  const text = readFileSync(absolute, "utf8");
  const lines = text ? text.split("\n").length - Number(text.endsWith("\n")) : 0;
  const isTest = path.startsWith("source/tests/") || path.startsWith("source/ui/tests/")
    || /\.(test|spec)\.[^.]+$/.test(path);
  totals[isTest ? "tests" : "application"] += lines;
}
console.log(`Application: ${totals.application} LOC; tests/helpers: ${totals.tests} LOC`);
if (totals.tests > totals.application) {
  console.error(`Test budget exceeded by ${totals.tests - totals.application} lines.`);
  process.exitCode = 1;
}
