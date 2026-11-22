import assert from "node:assert/strict";
import test from "node:test";
import { connection, deferred, flush, pending, setup } from "./ProviderSettingsFixtures.ts";
import type { ProviderConnection } from "../../../providers/ProviderConnection.ts";

void test("chooser reserves a tab before creation and survives its visibility change", async () => {
  const created = deferred<ProviderConnection>();
  let closed = 0;
  const tab = { opener: {}, location: { href: "about:blank" }, close: () => closed++ };
  let opens = 0;
  Object.defineProperty(globalThis, "window", {
    configurable: true,
    value: {
      open: () => {
        opens++;
        return tab;
      },
    },
  });
  try {
    let listed = false;
    const { state } = setup({
      list: async () => (listed ? [connection] : []),
      createChatGpt: () => created.promise,
      beginSignIn: async () => pending,
      getSignIn: async () => ({ ...pending, status: "completed", authorizationUrl: null }),
    });
    state.setActivity(true, true);
    await flush();
    state.select(null);
    const creating = state.createAndSignIn();
    assert.equal(opens, 1);
    assert.equal(tab.opener, null);
    state.setActivity(true, false);
    listed = true;
    created.resolve(connection);
    await creating;
    assert.equal(tab.location.href, pending.authorizationUrl);
    assert.equal(closed, 0);
    state.setActivity(true, true);
    await flush();
    assert.equal(closed, 0);
    state.setActivity(false, false);
  } finally {
    Reflect.deleteProperty(globalThis, "window");
  }
});

void test("blocked sign-in exposes fallback; rejected start closes reserved tab", async () => {
  let closed = 0;
  let blocked = true;
  const tab = { opener: {}, location: { href: "" }, close: () => closed++ };
  Object.defineProperty(globalThis, "window", {
    configurable: true,
    value: { open: () => (blocked ? null : tab) },
  });
  try {
    const { state } = setup({ beginSignIn: async () => pending });
    state.setActivity(true, true);
    await flush();
    await state.beginSignIn(connection);
    assert.equal(state.snapshot.blockedSignIns[connection.id], true);
    state.dispose();
    blocked = false;
    const other = setup({
      beginSignIn: async () => {
        throw new Error("failed");
      },
    }).state;
    other.setActivity(true, true);
    await flush();
    await other.beginSignIn(connection);
    assert.equal(closed, 1);
    other.setActivity(false, false);
  } finally {
    Reflect.deleteProperty(globalThis, "window");
  }
});
