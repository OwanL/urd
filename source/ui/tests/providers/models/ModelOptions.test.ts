import assert from "node:assert/strict";
import test from "node:test";
import { ModelOptions } from "../../../providers/models/ModelOptions.ts";
import { replaceProviderOptions } from "../../../providers/models/useModelOptions.ts";
import type { ProviderConnection } from "../../../providers/ProviderConnection.ts";
import type { ProviderModelCatalog } from "../../../providers/ProviderModels.ts";
import type { ModelSettings } from "../../../providers/ModelSettings.ts";

const firstConnection: ProviderConnection = {
  id: "10000000-0000-0000-0000-000000000001",
  name: "First provider",
  route: "custom",
  api: "openAIResponses",
  endpoint: "http://127.0.0.1:1234/v1/",
  authentication: "none",
};
const currentConnection: ProviderConnection = { ...firstConnection, name: "Current provider" };
const secondConnection: ProviderConnection = {
  ...firstConnection,
  id: "10000000-0000-0000-0000-000000000002",
  name: "Second provider",
};

function catalog(
  connectionId: string,
  id: string,
  name: string,
  refreshedAt: string | null = null,
): ProviderModelCatalog {
  return { connectionId, refreshedAt, models: [{ id, name }] };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((complete) => {
    resolve = complete;
  });
  return { promise, resolve };
}

void test("cached reads publish options and discover only providers without a refreshed catalog", async () => {
  const discoveryIds: string[] = [];
  const models = new ModelOptions({
    list: async () => [firstConnection, secondConnection],
    getModels: async (connectionId) =>
      catalog(
        connectionId,
        "cached",
        "Cached model",
        connectionId === firstConnection.id ? "2026-01-01T00:00:00Z" : null,
      ),
    getModelSettings: async () => [],
    refreshModels: async (connectionId) => {
      discoveryIds.push(connectionId);
      return catalog(connectionId, "discovered", "Discovered model");
    },
  });

  const cached = await models.readCached(new AbortController().signal);
  assert.equal(cached.hasProviders, true);
  assert.deepEqual(
    cached.options?.map(({ key }) => key),
    [`${firstConnection.id}/cached`, `${secondConnection.id}/cached`],
  );
  assert.equal(cached.options?.[0]?.providerLabel, "First provider");
  assert.equal(cached.options?.[0]?.selection.family, "other");
  assert.equal(cached.options?.[0]?.selection.supportsImages, false);
  assert.equal(cached.options?.[0]?.selection.contextWindow, null);
  assert.equal(cached.options?.[0]?.selection.reasoningEffort, "high");
  assert.deepEqual(
    cached.providersToRefresh.map(({ id }) => id),
    [secondConnection.id],
  );
  assert.deepEqual(discoveryIds, []);

  const missingOnly = await models.discover(
    cached.providersToRefresh,
    cached.revision,
    new AbortController().signal,
  );
  assert.deepEqual(discoveryIds, [secondConnection.id]);
  assert.deepEqual(
    missingOnly.options?.map(({ key }) => key),
    [`${secondConnection.id}/discovered`],
  );
  const merged = replaceProviderOptions(
    cached.options!,
    missingOnly.options!,
    cached.providers!,
    missingOnly.successfulProviderIds,
  );
  assert.deepEqual(
    merged.map(({ key }) => key),
    [`${firstConnection.id}/cached`, `${secondConnection.id}/discovered`],
  );

  const refreshed = await models.refresh();
  assert.deepEqual(discoveryIds, [secondConnection.id, firstConnection.id, secondConnection.id]);
  assert.equal(refreshed.options?.length, 2);
});

void test("cached read failures preserve successful providers and mark failed providers for discovery", async () => {
  const models = new ModelOptions({
    list: async () => [firstConnection, secondConnection],
    getModels: async (connectionId) => {
      if (connectionId === secondConnection.id) throw new Error("cached read failed");
      return catalog(connectionId, "cached", "Cached model", "refreshed");
    },
    getModelSettings: async () => [],
    refreshModels: async (connectionId) => catalog(connectionId, "found", "Found model"),
  });

  const cached = await models.readCached(new AbortController().signal);
  assert.equal(cached.error instanceof Error, true);
  assert.deepEqual(
    cached.options?.map(({ key }) => key),
    [`${firstConnection.id}/cached`],
  );
  assert.deepEqual(
    cached.providersToRefresh.map(({ id }) => id),
    [secondConnection.id],
  );
  assert.deepEqual(cached.successfulProviderIds, [firstConnection.id]);
});

void test("discovery failures do not discard options from successful providers", async () => {
  const models = new ModelOptions({
    list: async () => [firstConnection, secondConnection],
    getModels: async () => catalog(firstConnection.id, "cached", "Cached model"),
    getModelSettings: async () => [],
    refreshModels: async (connectionId) => {
      if (connectionId === secondConnection.id) throw new Error("discovery failed");
      return catalog(connectionId, "found", "Found model");
    },
  });

  const result = await models.refresh();
  assert.equal(result.error instanceof Error, true);
  assert.equal((result.error as Error).message, "discovery failed");
  assert.deepEqual(
    result.options?.map(({ key }) => key),
    [`${firstConnection.id}/found`],
  );
  assert.deepEqual(result.successfulProviderIds, [firstConnection.id]);
});

void test("empty provider lists are known and do not attempt discovery", async () => {
  let discoveries = 0;
  const models = new ModelOptions({
    list: async () => [],
    getModels: async () => {
      throw new Error("unexpected cache read");
    },
    getModelSettings: async () => [],
    refreshModels: async () => {
      discoveries++;
      return catalog(firstConnection.id, "unexpected", "Unexpected");
    },
  });

  const cached = await models.readCached(new AbortController().signal);
  assert.equal(cached.hasProviders, false);
  assert.deepEqual(cached.options, []);
  assert.deepEqual(cached.providersToRefresh, []);
  const refreshed = await models.refresh();
  assert.equal(refreshed.hasProviders, false);
  assert.deepEqual(refreshed.options, []);
  assert.equal(discoveries, 0);
});

void test("provider changes can rediscover every provider", async () => {
  const discoveryIds: string[] = [];
  const models = new ModelOptions({
    list: async () => [firstConnection, secondConnection],
    getModels: async (connectionId) => catalog(connectionId, "cached", "Cached", "refreshed"),
    getModelSettings: async () => [],
    refreshModels: async (connectionId) => {
      discoveryIds.push(connectionId);
      return catalog(connectionId, "discovered", "Discovered");
    },
  });

  models.invalidate();
  const cached = await models.readCached(new AbortController().signal);
  assert.deepEqual(cached.providersToRefresh, []);
  const discovered = await models.discover(cached.providers!, cached.revision);
  assert.deepEqual(discoveryIds, [firstConnection.id, secondConnection.id]);
  assert.equal(discovered.options?.length, 2);
});

void test("provider changes prevent an older manual refresh from publishing over the new cache", async () => {
  const discovery = deferred<ProviderModelCatalog>();
  const discoveryStarted = deferred<void>();
  let listCalls = 0;
  const models = new ModelOptions({
    list: async () => [listCalls++ === 0 ? firstConnection : currentConnection],
    getModels: async () => catalog(currentConnection.id, "cached", "Cached model"),
    getModelSettings: async () => [],
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

void test("configured metadata is matched by connection and model without inference or mutation", async () => {
  const connection = Object.freeze({ ...firstConnection });
  const settings: readonly ModelSettings[] = Object.freeze([
    Object.freeze({
      connectionId: connection.id,
      modelId: "gpt-configured",
      family: "anthropic" as const,
      supportsImages: true,
      contextWindow: 16_384,
    }),
    Object.freeze({
      connectionId: "another-connection",
      modelId: "gpt-unconfigured",
      family: "google" as const,
      supportsImages: true,
      contextWindow: null,
    }),
  ]);
  let records = settings;
  const models = new ModelOptions({
    list: async () => [connection],
    getModels: async () => ({
      connectionId: connection.id,
      refreshedAt: null,
      models: Object.freeze([
        Object.freeze({ id: "gpt-configured", name: "GPT configured", contextWindow: 32_768 }),
        Object.freeze({ id: "claude-default", name: "Claude default", contextWindow: 64_000 }),
        Object.freeze({ id: "gpt-unconfigured", name: "GPT unmatched", contextWindow: null }),
      ]),
    }),
    getModelSettings: async () => records,
    refreshModels: async () => catalog(connection.id, "unused", "Unused"),
  });

  const result = await models.readCached(new AbortController().signal);
  assert.deepEqual(
    result.options?.map(({ selection }) => [
      selection.family,
      selection.supportsImages,
      selection.contextWindow,
    ]),
    [
      ["anthropic", true, 16_384],
      ["other", false, 64_000],
      ["other", false, null],
    ],
  );
  assert.equal(result.options?.[0]?.selection.connection, connection);
  assert.equal(Object.isFrozen(connection), true);
  assert.equal(Object.isFrozen(settings[0]), true);
  const chosen = result.options![0]!.selection;
  records = [{ ...settings[0]!, family: "google", supportsImages: false, contextWindow: null }];
  const updated = await models.readCached(new AbortController().signal);
  assert.equal(updated.options?.[0]?.selection.family, "google");
  assert.equal(updated.options?.[0]?.selection.supportsImages, false);
  assert.equal(chosen.family, "anthropic");
  assert.equal(chosen.supportsImages, true);
  assert.equal(chosen.contextWindow, 16_384);
  assert.equal(updated.options?.[0]?.selection.contextWindow, 32_768);
});
