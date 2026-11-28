import assert from "node:assert/strict";
import test from "node:test";
import type { ModelSettings } from "../../../providers/ModelSettings.ts";
import type { ProviderConnection } from "../../../providers/ProviderConnection.ts";
import type { ProviderModelCatalog } from "../../../providers/ProviderModels.ts";
import type { ProviderSignIn } from "../../../providers/ProviderSignIn.ts";
import {
  connection,
  credential,
  emptyCatalog,
  flush,
  pending,
  setup,
} from "./ProviderSettingsFixtures.ts";

void test("Copilot catalog failure retains sign-in and clears only after an acknowledged retry", async (context) => {
  const copilot: ProviderConnection = { ...connection, route: "gitHubCopilot" };
  const completed: ProviderSignIn = {
    ...pending,
    status: "completed",
    authorizationUrl: null,
    catalogRefreshFailed: true,
  };
  const refreshedCatalog = { ...emptyCatalog, refreshedAt: "2026-11-28T01:00:00Z" };
  let currentCatalog: ProviderModelCatalog = emptyCatalog;
  let refreshes = 0;
  const { state } = setup({
    list: async () => [copilot],
    beginSignIn: async () => pending,
    getSignIn: async () => completed,
    getCredentials: async () => ({ ...credential, hasCredentials: true, requiresSignIn: false }),
    getModels: async () => currentCatalog,
    refreshModels: async () => {
      refreshes++;
      currentCatalog = refreshedCatalog;
      return currentCatalog;
    },
  });
  context.after(() => state.dispose());
  state.setActivity(true, true);
  await flush();
  await state.beginSignIn(copilot, true);
  assert.equal(state.snapshot.signIns[copilot.id]?.status, "completed");
  assert.equal(state.snapshot.signIns[copilot.id]?.catalogRefreshFailed, true);
  assert.equal(state.snapshot.credentials[copilot.id]?.hasCredentials, true);
  assert.equal(refreshes, 0);
  await state.refreshModels(copilot);
  assert.equal(refreshes, 1);
  assert.equal(state.snapshot.signIns[copilot.id]?.status, "completed");
  assert.equal(state.snapshot.signIns[copilot.id]?.catalogRefreshFailed, false);
});

void test("model metadata drafts survive reads, refresh and disconnect; saves are explicit and removal clears them", async (context) => {
  let records: readonly ModelSettings[] = [
    {
      connectionId: "one",
      modelId: "opaque",
      family: "anthropic",
      supportsImages: true,
      contextWindow: null,
    },
  ];
  let connections = [connection];
  const writes: ModelSettings[] = [];
  const { state } = setup({
    list: async () => connections,
    getModelSettings: async () => records,
    saveModelSettings: async (settings) => {
      writes.push(settings);
      records = [settings];
      return settings;
    },
    refreshModels: async () => emptyCatalog,
    disconnect: async () => ({
      connectionId: "one",
      localCredentialsCleared: true,
      remoteRevocationConfirmed: false,
    }),
    remove: async () => {
      connections = [];
    },
  });
  context.after(() => state.setActivity(false, false));
  state.setActivity(true, true);
  await flush();
  const initial = records[0]!;
  state.editModel(initial, { family: "google", contextWindow: 32_768 });
  await state.read();
  await state.refreshModels(connection);
  state.requestConfirmation("disconnect", connection);
  await state.confirm();
  assert.deepEqual(state.snapshot.modelSettings.one, [initial]);
  assert.equal(state.snapshot.modelDrafts.one?.[0]?.family, "google");
  assert.equal(state.snapshot.modelDrafts.one?.[0]?.contextWindow, 32_768);
  assert.equal(writes.length, 0);
  const draft = state.snapshot.modelDrafts.one![0]!;
  await state.saveModelSettings(draft);
  assert.deepEqual(writes, [{ ...initial, family: "google", contextWindow: 32_768 }]);
  assert.deepEqual(state.snapshot.modelSettings.one, writes);
  assert.deepEqual(state.snapshot.modelDrafts.one, []);
  state.requestConfirmation("remove", connection);
  await state.confirm();
  assert.equal(state.snapshot.modelSettings.one, undefined);
  assert.equal(state.snapshot.modelDrafts.one, undefined);
});
