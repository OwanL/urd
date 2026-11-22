import assert from "node:assert/strict";
import test from "node:test";
import type { ProviderConnection } from "../../../providers/ProviderConnection.ts";
import { connection, deferred, flush, pending, setup } from "./ProviderSettingsFixtures.ts";

void test("chooser starts sign-in when opening the reservation immediately hides the app", async () => {
  const created = deferred<ProviderConnection>();
  let starts = 0;
  const tab = { opener: {}, location: { href: "about:blank" }, close: () => {} };
  const { state } = setup({
    list: async () => [],
    createChatGpt: () => created.promise,
    beginSignIn: async () => {
      starts++;
      return pending;
    },
  });
  Object.defineProperty(globalThis, "window", {
    configurable: true,
    value: {
      open: () => {
        state.setActivity(true, false);
        return tab;
      },
    },
  });
  try {
    state.setActivity(true, true);
    await flush();
    state.select(null);
    const creating = state.createAndSignIn();
    created.resolve(connection);
    await creating;
    assert.equal(starts, 1);
    assert.equal(tab.location.href, pending.authorizationUrl);
    state.dispose();
  } finally {
    Reflect.deleteProperty(globalThis, "window");
  }
});

void test("disposal closes an unnavigated reservation and prevents later navigation", async () => {
  const started = deferred<typeof pending>();
  let closed = 0;
  const tab = { opener: {}, location: { href: "about:blank" }, close: () => closed++ };
  Object.defineProperty(globalThis, "window", { configurable: true, value: { open: () => tab } });
  try {
    const { state } = setup({ beginSignIn: () => started.promise });
    state.setActivity(true, true);
    await flush();
    const signIn = state.beginSignIn(connection);
    state.dispose();
    assert.equal(closed, 1);
    started.resolve(pending);
    await signIn;
    assert.equal(closed, 1);
    assert.equal(tab.location.href, "about:blank");
  } finally {
    Reflect.deleteProperty(globalThis, "window");
  }
});
