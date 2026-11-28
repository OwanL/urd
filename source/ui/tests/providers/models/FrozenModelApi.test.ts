import assert from "node:assert/strict";
import test from "node:test";
import { isSelection } from "../../../chat/ChatState.ts";
import { sameContextModel } from "../../../providers/LlmSelection.ts";
import { ModelOptions } from "../../../providers/models/ModelOptions.ts";
import type { ProviderConnection } from "../../../providers/ProviderConnection.ts";
import type { ProviderModelCatalog } from "../../../providers/ProviderModels.ts";

void test("picker freezes Copilot protocol without rewriting the provider binding", async () => {
  const copilot: ProviderConnection = {
    id: "10000000-0000-0000-0000-000000000001",
    name: "GitHub Copilot",
    route: "gitHubCopilot",
    api: "openAIResponses",
    endpoint: "https://api.individual.githubcopilot.com/",
    authentication: "oAuth",
  };
  const custom: ProviderConnection = {
    ...copilot,
    id: "10000000-0000-0000-0000-000000000002",
    name: "Custom",
    route: "custom",
    endpoint: "https://fixture.invalid/",
    authentication: "none",
  };
  const readCatalog = (id: string): ProviderModelCatalog => ({
    connectionId: id,
    refreshedAt: "2026-01-01T00:00:00Z",
    models: [
      { id: "model", name: "Supported", api: "anthropicMessages" },
      { id: "no-protocol", name: "Missing protocol" },
    ],
  });
  const options = new ModelOptions({
    list: async () => [copilot, custom],
    getModels: async (id) => readCatalog(id),
    getModelSettings: async () => [],
    getCredentials: async () => {
      throw new Error("unexpected credential read");
    },
    refreshModels: async (id) => ({
      ...readCatalog(id),
      models: [{ id: "model", name: "Supported", api: "openAIChatCompletions" }],
    }),
  });
  const cached = await options.readCached(new AbortController().signal);
  assert.deepEqual(
    cached.options?.map(({ selection }) => [selection.connection.id, selection.modelId]),
    [
      [copilot.id, "model"],
      [custom.id, "model"],
      [custom.id, "no-protocol"],
    ],
  );
  const oldSelection = cached.options![0]!.selection;
  assert.equal(oldSelection.connection, copilot);
  assert.equal(oldSelection.connection.api, "openAIResponses");
  assert.equal(oldSelection.modelApi, "anthropicMessages");
  assert.equal(isSelection(oldSelection), true);
  assert.equal(isSelection({ ...oldSelection, modelApi: null }), false);
  assert.equal(isSelection({ ...oldSelection, modelApi: "unknownApi" }), false);
  assert.equal(
    isSelection({ ...oldSelection, connection: { ...copilot, api: "anthropicMessages" } }),
    false,
  );
  const legacy = cached.options![1]!.selection;
  assert.equal(legacy.modelApi, undefined);
  assert.equal(isSelection(legacy), true);
  assert.equal(isSelection({ ...legacy, modelApi: "anthropicMessages" }), false);
  assert.equal(sameContextModel(legacy, { ...legacy, modelApi: null }), true);

  const refreshed = await options.refresh();
  const newSelection = refreshed.options![0]!.selection;
  assert.equal(newSelection.modelApi, "openAIChatCompletions");
  assert.equal(oldSelection.modelApi, "anthropicMessages");
  assert.equal(sameContextModel(oldSelection, newSelection), false);
  assert.equal(sameContextModel(oldSelection, { ...oldSelection }), true);
});
