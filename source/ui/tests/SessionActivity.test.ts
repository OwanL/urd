import assert from "node:assert/strict";
import test from "node:test";
import {
  emptySessionActivity,
  isBusySessionStatus,
  recordSessionStatus,
  resetSessionTransitionStatuses,
  retainOpenSessionActivity,
} from "../app/SessionActivity.ts";

void test("only an observed busy-to-idle transition marks an inactive session unread", () => {
  for (const status of ["running", "waiting", "compacting"])
    assert.equal(isBusySessionStatus(status), true);
  assert.equal(isBusySessionStatus("idle"), false);

  let activity = recordSessionStatus(emptySessionActivity, "chat", "running", null);
  assert.equal(activity.unread.has("chat"), false);
  activity = recordSessionStatus(activity, "chat", "idle", null);
  assert.equal(activity.unread.has("chat"), true);

  // Idle registration snapshots, including reconnect handshakes, are not completions.
  activity = recordSessionStatus(emptySessionActivity, "idle-chat", "idle", null);
  assert.equal(activity.unread.has("idle-chat"), false);
  activity = recordSessionStatus(activity, "idle-chat", "idle", null);
  assert.equal(activity.unread.has("idle-chat"), false);

  const disconnectedBusy = recordSessionStatus(
    emptySessionActivity,
    "reconnected",
    "running",
    null,
  );
  activity = resetSessionTransitionStatuses(disconnectedBusy);
  assert.equal(activity.statuses.reconnected, "running");
  activity = recordSessionStatus(activity, "reconnected", "idle", null);
  assert.equal(activity.unread.has("reconnected"), false);
});

void test("opening or closing a session clears its unread marker", () => {
  let activity = recordSessionStatus(emptySessionActivity, "opened", "running", null);
  activity = recordSessionStatus(activity, "opened", "idle", null);
  activity = retainOpenSessionActivity(activity, ["opened"], ["opened"], "opened");
  assert.equal(activity.unread.has("opened"), false);

  activity = recordSessionStatus(activity, "closed", "running", null);
  activity = recordSessionStatus(activity, "closed", "idle", null);
  activity = retainOpenSessionActivity(activity, ["opened"], ["opened"], "opened");
  assert.equal(activity.unread.has("closed"), false);
  assert.equal("closed" in activity.statuses, false);
});

void test("active completions are not marked unread and existing markers survive other updates", () => {
  let activity = recordSessionStatus(emptySessionActivity, "active", "running", "active");
  activity = recordSessionStatus(activity, "active", "idle", "active");
  assert.equal(activity.unread.has("active"), false);

  activity = recordSessionStatus(activity, "background", "running", "active");
  activity = recordSessionStatus(activity, "background", "idle", "active");
  activity = recordSessionStatus(activity, "active", "idle", "active");
  assert.equal(activity.unread.has("background"), true);
  assert.equal(activity.unread.has("active"), false);
  activity = recordSessionStatus(activity, "background", "running", "active");
  assert.equal(activity.unread.has("background"), false);
});

void test("status snapshots are retained only for observed sessions while unread stays bounded to open tabs", () => {
  let activity = recordSessionStatus(emptySessionActivity, "observed", "running", null);
  activity = recordSessionStatus(activity, "observed", "idle", null);
  activity = recordSessionStatus(activity, "paused", "running", null);

  activity = retainOpenSessionActivity(activity, ["observed", "paused"], ["observed"], null);
  assert.equal(activity.statuses.observed, "idle");
  assert.equal("observed" in activity.transitionStatuses, true);
  assert.equal(activity.statuses.paused, "running");
  assert.equal("paused" in activity.transitionStatuses, false);
  assert.deepEqual([...activity.unread], ["observed"]);
});
