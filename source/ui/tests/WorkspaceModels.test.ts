import assert from "node:assert/strict";
import test from "node:test";
import { WorkspaceModels } from "../app/WorkspaceModels.ts";
import type { ProviderConnection } from "../providers/ProviderConnection.ts";
import type { ProviderModelCatalog } from "../providers/ProviderModels.ts";

const firstConnection: ProviderConnection = {
  id: "10000000-0000-0000-0000-000000000001",
  name: "First provider",
  route: "custom",
  api: "openAIResponses",
  endpoint: "http://127.0.0.1:1234/v1/",
  authentication: "none",
};
const currentConnection: ProviderConnection = { ...firstConnection, name: "Current provider" };

function catalog(connectionId: string, id: string, name: string): ProviderModelCatalog {
  return { connectionId, refreshedAt: null, models: [{ id, name }] };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((complete) => {
    resolve = complete;
  });
  return { promise, resolve };
}

void test("cached reads do not discover models; explicit refresh does", async () => {
  let cachedReads = 0;
  let discoveries = 0;
  const models = new WorkspaceModels({
    list: async () => [firstConnection],
    getModels: async () => {
      cachedReads++;
      return catalog(firstConnection.id, "cached", "Cached model");
    },
    refreshModels: async () => {
      discoveries++;
      return catalog(firstConnection.id, "discovered", "Discovered model");
    },
  });

  const cached = await models.readCached(new AbortController().signal);
  assert.equal(cached.options?.[0]?.key, `${firstConnection.id}/cached`);
  assert.equal(cached.options?.[0]?.providerLabel, "First provider");
  assert.equal(cachedReads, 1);
  assert.equal(discoveries, 0);

  const refreshed = await models.refresh();
  assert.equal(refreshed.options?.[0]?.key, `${firstConnection.id}/discovered`);
  assert.equal(discoveries, 1);
});

void test("provider changes prevent an older manual refresh from publishing over the new cache", async () => {
  const discovery = deferred<ProviderModelCatalog>();
  const discoveryStarted = deferred<void>();
  let listCalls = 0;
  const models = new WorkspaceModels({
    list: async () => [listCalls++ === 0 ? firstConnection : currentConnection],
    getModels: async () => catalog(currentConnection.id, "cached", "Cached model"),
    refreshModels: async () => {
      discoveryStarted.resolve();
      return discovery.promise;
    },
  });

  const manual = models.refresh();
  await discoveryStarted.promise;
  models.invalidate();
  const cached = await models.readCached(new AbortController().signal);
  assert.equal(models.isCurrent(cached.revision), true);
  assert.equal(cached.options?.[0]?.providerLabel, "Current provider");

  discovery.resolve(catalog(firstConnection.id, "old", "Old discovery"));
  const oldRefresh = await manual;
  assert.equal(models.isCurrent(oldRefresh.revision), false);
  assert.equal(oldRefresh.options, null);
});
