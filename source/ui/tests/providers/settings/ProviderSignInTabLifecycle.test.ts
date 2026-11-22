import assert from "node:assert/strict";
import test from "node:test";
import type { ProviderConnection } from "../../../providers/ProviderConnection.ts";
import { connection, deferred, flush, pending, setup } from "./ProviderSettingsFixtures.ts";

void test("navigation during creation closes the reservation without starting sign-in", async () => {
  const created = deferred<ProviderConnection>();
  let closed = 0;
  let starts = 0;
  const tab = { opener: {}, location: { href: "" }, close: () => closed++ };
  Object.defineProperty(globalThis, "window", { configurable: true, value: { open: () => tab } });
  try {
    const { state } = setup({
      list: async () => [],
      createChatGpt: () => created.promise,
      beginSignIn: async () => {
        starts++;
        return pending;
      },
    });
    state.setActivity(true, true);
    await flush();
    state.select(null);
    const creating = state.createAndSignIn();
    state.setActivity(false, false);
    state.setActivity(true, true);
    created.resolve(connection);
    await creating;
    assert.equal(starts, 0);
    assert.equal(closed, 1);
    state.setActivity(false, false);
  } finally {
    Reflect.deleteProperty(globalThis, "window");
  }
});

void test("cancelling a detail sign-in leaves the navigated tab alone", async () => {
  let closed = 0;
  const tab = { opener: {}, location: { href: "" }, close: () => closed++ };
  Object.defineProperty(globalThis, "window", { configurable: true, value: { open: () => tab } });
  try {
    const { state } = setup({
      beginSignIn: async () => pending,
      cancelSignIn: async () => ({ ...pending, status: "cancelled", authorizationUrl: null }),
    });
    state.setActivity(true, true);
    await flush();
    await state.beginSignIn(connection);
    assert.equal(tab.location.href, pending.authorizationUrl);
    assert.equal(state.snapshot.blockedSignIns[connection.id], false);
    await state.cancelSignIn(pending);
    assert.equal(closed, 0);
    state.setActivity(false, false);
  } finally {
    Reflect.deleteProperty(globalThis, "window");
  }
});
