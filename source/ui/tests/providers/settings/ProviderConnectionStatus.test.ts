import assert from "node:assert/strict";
import test from "node:test";
import {
  credentialState,
  matchesConnection,
  providerHostname,
  providerReady,
  providerSetupState,
} from "../../../providers/settings/ProviderConnectionStatus.ts";
import { connection, credential, emptyCatalog, flush, setup } from "./ProviderSettingsFixtures.ts";

void test("readiness handles authentication, credential health and disconnects", () => {
  const anonymous = { ...connection, authentication: "none" } as const;
  const connected = { ...credential, hasCredentials: true, requiresSignIn: false };
  assert.equal(providerReady(connection, undefined), false);
  assert.equal(providerReady(connection, credential), false);
  assert.equal(providerReady(connection, { ...connected, requiresSignIn: true }), false);
  assert.equal(providerReady(connection, { ...connected, planAccessGranted: false }), false);
  assert.equal(providerReady(connection, { ...connected, isDisconnecting: true }), false);
  assert.equal(providerReady(connection, connected), true);
  assert.equal(providerReady(anonymous, undefined), true);
  assert.equal(providerReady(anonymous, credential), true);
  assert.equal(providerReady(anonymous, { ...credential, isDisconnecting: true }), false);
});

void test("setup membership uses saved credentials, not current provider health", () => {
  const apiKeyConnection = { ...connection, authentication: "apiKey" } as const;
  assert.equal(
    providerSetupState({ ...connection, authentication: "none" }, undefined),
    "complete",
  );
  assert.equal(providerSetupState(apiKeyConnection, undefined), "unknown");
  assert.equal(providerSetupState(apiKeyConnection, credential), "incomplete");
  assert.equal(
    providerSetupState(apiKeyConnection, {
      ...credential,
      hasCredentials: true,
      requiresSignIn: false,
      expiresAt: "2000-01-01T00:00:00Z",
      planAccessGranted: false,
      isDisconnecting: true,
    }),
    "complete",
  );
});

void test("generic custom connections default to their hostname and keep explicit names on save", async () => {
  assert.equal(
    providerHostname("https://user:secret@API.Example:443/v1/?key=value"),
    "api.example",
  );
  assert.equal(providerHostname("http://localhost:1234/v1/"), "localhost");
  assert.equal(providerHostname("not a URL"), "");
  const custom = { ...connection, route: "custom", authentication: "apiKey" } as const;
  const names: string[] = [];
  const { state } = setup({
    list: async () => [custom],
    createChatGpt: async (name) => {
      names.push(name);
      return connection;
    },
    createOpenAI: async (name) => {
      names.push(name);
      return connection;
    },
    createCustom: async (settings) => {
      names.push(settings.name);
      return { ...custom, ...settings };
    },
    updateCustom: async (_id, settings) => {
      names.push(settings.name);
      return { ...custom, ...settings };
    },
  });
  state.setActivity(true, true);
  await flush();
  assert.equal(state.snapshot.connections[0]?.name, "Work");
  await state.create();
  state.chooseProvider("openAI");
  await state.create();
  state.chooseProvider("custom");
  state.editAdd({ endpoint: "https://custom.example:1234/v1/" });
  await state.create();
  assert.equal(state.snapshot.connections[0]?.name, "Work");
  await state.save(custom);
  assert.deepEqual(names, ["api.openai.com", "api.openai.com", "custom.example", "Work"]);
  state.setActivity(false, false);
});

void test("model discovery is explicit and credential summaries distinguish missing from connected", async () => {
  let refreshes = 0;
  const { state } = setup({
    refreshModels: async () => {
      refreshes++;
      return emptyCatalog;
    },
  });
  state.setActivity(true, true);
  await flush();
  assert.equal(refreshes, 0);
  await state.refreshModels(connection);
  assert.equal(refreshes, 1);
  assert.equal(credentialState(connection, undefined), "unknown");
  assert.equal(credentialState(connection, credential), "noCredentials");
  assert.equal(
    credentialState(connection, { ...credential, hasCredentials: true, requiresSignIn: false }),
    "connected",
  );
  assert.equal(matchesConnection(connection, "CHATGPT"), true);
  assert.equal(matchesConnection(connection, "provider.example"), true);
  assert.equal(matchesConnection(connection, "secret"), false);
  state.setActivity(false, false);
});
