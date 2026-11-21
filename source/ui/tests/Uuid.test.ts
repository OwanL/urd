import assert from "node:assert/strict";
import test from "node:test";
import { newUuid } from "../Uuid.ts";

void test("newUuid needs only getRandomValues, not secure-context-only randomUUID", () => {
  let filledBytes = 0;
  const id = newUuid((bytes) => {
    filledBytes = bytes.length;
    bytes.set(Array.from({ length: bytes.length }, (_, index) => index));
  });

  assert.equal(filledBytes, 16);
  assert.equal(id, "00010203-0405-4607-8809-0a0b0c0d0e0f");
});
