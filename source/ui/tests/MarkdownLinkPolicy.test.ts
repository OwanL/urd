import assert from "node:assert/strict";
import test from "node:test";
import { safeMarkdownLink } from "../markdown-rendering/MarkdownLinkPolicy.ts";

void test("allows only safe absolute and relative Markdown links", () => {
  for (const destination of [
    "https://example.test/path",
    "HTTP://example.test/path",
    "mailto:user@example.test",
    "relative/path?query=1#section",
    "/root/path",
    "?query=1",
    "#section",
  ])
    assert.equal(safeMarkdownLink(destination), destination);

  for (const destination of [
    undefined,
    "javascript:alert(1)",
    "data:text/html,unsafe",
    "ftp://example.test/file",
    "//example.test/path",
    "https:\\\\example.test/path",
    "https://example.test/line\nbreak",
    "https://example.test/\u0000",
    "https://example.test/\u0085",
  ])
    assert.equal(safeMarkdownLink(destination), undefined);
});
