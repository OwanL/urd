import assert from "node:assert/strict";
import test from "node:test";
import { newChat } from "../../chat/ChatState.ts";
import { initialSnooze, minuteMs } from "../../workspace/snooze/SnoozeDuration.ts";
import { rememberSnoozedChats } from "../../workspace/snooze/SnoozedWorkspace.ts";
import { emptyWorkspace } from "../../workspace/WorkspaceState.ts";

const id = "a7770000-0000-4000-8000-000000000001";
const other = "a7770000-0000-4000-8000-000000000002";
const until = "2026-11-23T20:00:00Z";

void test("the default is exactly 9 am local tomorrow, including after midnight or a clock change", () => {
  for (const now of [
    new Date(2026, 10, 23, 15, 25, 53),
    new Date(2026, 11, 31, 23, 59, 45),
    new Date(2026, 2, 7, 20, 15),
  ]) {
    const { from, minutes } = initialSnooze(now);
    const tomorrow = new Date(now.getFullYear(), now.getMonth(), now.getDate() + 1, 9);
    assert.equal(from + minutes * minuteMs, tomorrow.getTime());
    assert.ok(minutes > 0);
  }
});

void test("hiding preserves the tab and draft; restoring doesn't select it again", () => {
  const state = {
    ...emptyWorkspace(),
    activeId: id,
    tabs: [id, other],
    chats: {
      [id]: { ...newChat(null), draft: "Keep this", draftVersion: 1 },
      [other]: newChat(null),
    },
  };
  const snoozed = rememberSnoozedChats(state, [{ sessionId: id, title: null, until }], () => false);
  assert.equal(snoozed.activeId, other);
  assert.equal(snoozed.tabs, state.tabs);
  assert.equal(snoozed.chats, state.chats);
  assert.equal(
    rememberSnoozedChats(snoozed, [], () => false),
    snoozed,
  );
});

void test("a first snoozed chat is remembered without focusing it or recreating a closed chat", () => {
  const state = rememberSnoozedChats(
    emptyWorkspace(),
    [
      { sessionId: id, title: "Parked chat", until },
      { sessionId: other, title: "Closed chat", until },
    ],
    (session) => session === other,
  );
  assert.deepEqual(state.tabs, [id]);
  assert.equal(state.chats[id]?.title, "Parked chat");
  assert.equal(state.activeId, null);
});
