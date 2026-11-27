import assert from "node:assert/strict";
import { test } from "node:test";
import type { ToolDraft } from "../../sessions/SessionEvents.ts";
import {
  adoptToolDrafts,
  applyToolDraft,
  setToolDraftBoot,
  ToolDraftStore,
} from "../../sessions/ToolDrafts.ts";

const draft: ToolDraft = {
  session: "s",
  boot: "b",
  version: 2,
  executionId: "e",
  requestId: "r",
  callId: "c",
  entryId: "i",
  name: "read",
  order: 0,
  state: "drafting",
  argumentBytes: 4,
  head: "file",
  tail: "tail",
};

void test("store notifies only the watched session and retains unchanged snapshots and drafts", () => {
  const store = new ToolDraftStore();
  let first = 0;
  let second = 0;
  const unsubscribe = store.subscribe("s", () => first++);
  store.subscribe("other", () => second++);
  store.watch("s");
  store.boot("b");
  store.apply(draft);
  const snapshot = store.read("s");
  const count = first;
  store.apply({ ...draft, callId: "discarded", entryId: "discarded", state: "discarded" });
  assert.equal(store.read("s"), snapshot);
  assert.equal(first, count);
  store.apply({ ...draft, callId: "other", entryId: "other" });
  assert.equal(store.read("s")[0], snapshot[0]);
  assert.equal(second, 0);
  store.watch("other");
  assert.equal(store.read("s").length, 0);
  assert.equal(store.read("other").length, 0);
  assert.equal(second, 1);
  store.apply({ ...draft, session: "other" });
  assert.equal(store.read("other").length, 1);
  unsubscribe();
  const afterUnsubscribe = first;
  store.watch("s");
  assert.equal(first, afterUnsubscribe);
});

void test("rejects older versions per call, including after discard", () => {
  let state = setToolDraftBoot({ boot: null, items: [] }, "b");
  state = applyToolDraft(state, draft);
  state = applyToolDraft(state, { ...draft, callId: "other", version: 1 });
  state = applyToolDraft(state, { ...draft, version: 3, state: "discarded" });
  const discarded = state;
  state = applyToolDraft(state, draft);
  assert.equal(state, discarded);
  assert.equal(state.items.find((item) => item.callId === "c")?.state, "discarded");
  assert.equal(state.items.find((item) => item.callId === "other")?.version, 1);
});

void test("adoption removes matching drafts and prevents stale replay", () => {
  let state = setToolDraftBoot({ boot: null, items: [] }, "b");
  state = applyToolDraft(state, draft);
  state = applyToolDraft(state, { ...draft, callId: "other", entryId: "other" });
  state = adoptToolDrafts(state, "s", ["i"]);
  assert.deepEqual(
    state.items.map((item) => item.callId),
    ["other"],
  );
  state = applyToolDraft(state, { ...draft, version: 1 });
  state = applyToolDraft(state, draft);
  state = applyToolDraft(state, { ...draft, version: 3, state: "promoted" });
  assert.deepEqual(
    state.items.map((item) => item.callId),
    ["other"],
  );
  assert.equal(adoptToolDrafts(state, "other-session", ["other"]), state);
  assert.equal(state.adopted?.size, 1);
  state = applyToolDraft(state, { ...draft, callId: "discarded", state: "discarded" });
  state = applyToolDraft(state, { ...draft, requestId: "next", callId: "next", version: 4 });
  assert.equal(
    state.items.some((item) => item.callId === "discarded"),
    false,
  );
  assert.equal(state.adopted?.size ?? 0, 0);
  // Adopting an older visible call must not retain a record for the superseded request.
  state = adoptToolDrafts(state, "s", ["other"]);
  assert.equal(state.adopted?.size ?? 0, 0);
  state = adoptToolDrafts(state, "s", ["i"]);
  assert.equal(state.adopted?.size, 1);
});

void test("uses session versions to ignore delayed older requests without blocking same-request calls", () => {
  let state = setToolDraftBoot({ boot: null, items: [] }, "b");
  state = applyToolDraft(state, { ...draft, version: 8 });
  state = applyToolDraft(state, { ...draft, version: 9, state: "promoted" });
  state = applyToolDraft(state, { ...draft, requestId: "next", version: 12 });
  state = applyToolDraft(state, {
    ...draft,
    requestId: "next",
    callId: "next-call",
    entryId: "next-entry",
    version: 10,
  });
  const current = state;
  state = applyToolDraft(state, { ...draft, version: 11, state: "promoted" });
  assert.equal(state, current);
  assert.deepEqual(
    state.items.map((item) => `${item.requestId}:${item.callId}`),
    ["r:c", "next:c", "next:next-call"],
  );
});

void test("keeps promoted drafts across requests until their history entries are adopted", () => {
  let state = setToolDraftBoot({ boot: null, items: [] }, "b");
  state = applyToolDraft(state, { ...draft, order: 2 });
  state = applyToolDraft(state, {
    ...draft,
    callId: "promoted",
    entryId: "promoted",
    order: 3,
    state: "promoted",
  });
  state = applyToolDraft(state, {
    ...draft,
    requestId: "next",
    callId: "next",
    order: 0,
    version: 3,
  });
  state = applyToolDraft(state, {
    ...draft,
    requestId: "next",
    callId: "next2",
    order: 1,
    version: 3,
  });
  assert.deepEqual(
    state.items.map((item) => item.callId),
    ["c", "promoted", "next", "next2"],
  );
  state = adoptToolDrafts(state, "s", ["promoted"]);
  assert.deepEqual(
    state.items.map((item) => item.callId),
    ["c", "next", "next2"],
  );

  state = applyToolDraft(state, {
    ...draft,
    executionId: "new-execution",
    requestId: "next",
    callId: "new",
    state: "promoted",
    version: 4,
  });
  state = applyToolDraft(state, {
    ...draft,
    executionId: "new-execution",
    requestId: "later",
    callId: "later",
    version: 5,
  });
  assert.equal(
    state.items.some((item) => item.callId === "new"),
    true,
  );
});

for (const status of ["idle", "failed"] as const) {
  void test(`${status} keeps promoted drafts until their history entries are adopted`, () => {
    const store = new ToolDraftStore();
    store.watch("s");
    store.boot("b");
    store.apply(draft);
    store.apply({ ...draft, callId: "promoted", entryId: "promoted", state: "promoted" });
    store.status("other", "idle");
    store.status("s", status);
    assert.deepEqual(
      store.read("s").map((item) => item.callId),
      ["c", "promoted"],
    );
    store.adopt("s", ["promoted"]);
    assert.deepEqual(
      store.read("s").map((item) => item.callId),
      ["c"],
    );
  });

  void test(`${status} keeps a promotion received after status until history refresh`, () => {
    const store = new ToolDraftStore();
    store.watch("s");
    store.boot("b");
    store.apply(draft);
    store.status("s", status);
    store.apply({ ...draft, state: "promoted", head: "", tail: "" });
    assert.equal(store.read("s").length, 1);
    assert.equal(store.read("s")[0]?.state, "promoted");
    store.status("s", "running");
    assert.equal(store.read("s")[0]?.state, "promoted");
    store.adopt("s", ["i"]);
    assert.deepEqual(store.read("s"), []);
  });

  void test(`${status} does not adopt a newer active row for a rejected stale promotion`, () => {
    const store = new ToolDraftStore();
    store.watch("s");
    store.boot("b");
    store.apply({ ...draft, requestId: "new", callId: "newer", entryId: "shared", version: 3 });
    store.status("s", status);
    store.apply({
      ...draft,
      requestId: "old",
      callId: "stale",
      entryId: "shared",
      version: 2,
      state: "promoted",
    });
    assert.deepEqual(
      store.read("s").map((item) => item.callId),
      ["newer"],
    );
  });
}

void test("accepts same version updates and preserves preview on promotion", () => {
  let state = setToolDraftBoot({ boot: null, items: [] }, "b");
  state = applyToolDraft(state, draft);
  state = applyToolDraft(state, { ...draft, state: "promoted", head: "", tail: "" });
  assert.equal(state.items[0]?.head, "file");
  assert.equal(state.items[0]?.tail, "tail");
  assert.equal(state.items[0]?.state, "promoted");
});

void test("disconnect resets drafts before reconnecting and a new boot rejects old events", () => {
  const store = new ToolDraftStore();
  store.watch("s");
  store.boot("b");
  store.apply(draft);
  store.disconnect();
  assert.deepEqual(store.read("s"), []);
  store.apply(draft);
  assert.deepEqual(store.read("s"), []);
  store.boot("b");
  store.apply({ ...draft, version: 0 });
  assert.equal(store.read("s")[0]?.version, 0);
  store.boot("new");
  assert.deepEqual(store.read("s"), []);
  store.apply(draft);
  assert.deepEqual(store.read("s"), []);
  store.apply({ ...draft, boot: "new", version: 0 });
  assert.equal(store.read("s")[0]?.version, 0);
});
