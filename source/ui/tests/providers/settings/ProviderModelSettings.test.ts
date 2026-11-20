import assert from "node:assert/strict";
import test from "node:test";
import type { ModelSettings } from "../../../providers/ModelSettings.ts";
import { connection, emptyCatalog, flush, setup } from "./ProviderSettingsFixtures.ts";

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
