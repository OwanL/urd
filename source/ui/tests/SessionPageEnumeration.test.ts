import assert from "node:assert/strict";
import test from "node:test";
import { enumerateSessionPages } from "../app/enumerateSessionPages.ts";
import type { SessionIdPage } from "../sessions/SessionIdPage.ts";

void test("session page enumeration preserves cursors and stops after the final page", async () => {
  const opaqueCursor = "opaque /+?&=#%雪";
  const pages: SessionIdPage[] = [
    { sessionIds: ["first"], nextCursor: opaqueCursor },
    { sessionIds: ["second"], nextCursor: null },
  ];
  const cursors: (string | undefined)[] = [];
  const controller = new AbortController();
  const sessionIds: string[] = [];
  const client = {
    list(cursor?: string) {
      cursors.push(cursor);
      return Promise.resolve(pages[cursors.length - 1]!);
    },
  };

  for await (const page of enumerateSessionPages(client, controller.signal))
    sessionIds.push(...page.sessionIds);

  assert.deepEqual(cursors, [undefined, opaqueCursor]);
  assert.deepEqual(sessionIds, ["first", "second"]);
});

void test("session page enumeration discards a page completed after cancellation", async () => {
  const controller = new AbortController();
  let complete!: (page: SessionIdPage) => void;
  const client = {
    list: () => new Promise<SessionIdPage>((resolve) => (complete = resolve)),
  };
  const nextPage = enumerateSessionPages(client, controller.signal)[Symbol.asyncIterator]().next();

  controller.abort();
  complete({ sessionIds: ["ignored"], nextCursor: null });

  assert.deepEqual(await nextPage, { value: undefined, done: true });
});
