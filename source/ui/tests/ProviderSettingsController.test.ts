import assert from "node:assert/strict";
import test from "node:test";
import { RequestError } from "../http/RequestError.ts";
import type { BucketSuggestions } from "../providers/ProviderClient.ts";
import type { ProviderConnection } from "../providers/ProviderConnection.ts";
import type { ProviderCredentialStatus } from "../providers/ProviderCredentials.ts";
import type { ProviderSignIn } from "../providers/ProviderSignIn.ts";
import type { ModelSettings } from "../providers/ModelSettings.ts";
import {
  credentialState,
  matchesConnection,
  providerHostname,
  providerSetupState,
  ProviderSettingsController,
} from "../providers/settings/ProviderSettingsController.ts";
import type { ProviderSettingsClient } from "../providers/settings/ProviderSettingsController.ts";

const connection: ProviderConnection = {
  id: "one",
  name: "Work",
  route: "chatGpt",
  api: "openAIResponses",
  endpoint: "https://provider.example/v1/",
  authentication: "oAuth",
};
const credential: ProviderCredentialStatus = {
  connectionId: "one",
  hasCredentials: false,
  requiresSignIn: true,
  accountLabel: null,
  expiresAt: null,
  planAccessGranted: null,
  isDisconnecting: false,
};
const pending: ProviderSignIn = {
  id: "attempt",
  connectionId: "one",
  status: "pending",
  authorizationUrl: "https://auth.example/authorize",
  expiresAt: "2026-11-09T00:00:00Z",
  error: null,
};
const emptyCatalog = { connectionId: "one", refreshedAt: null, models: [] } as const;

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((accept, fail) => {
    resolve = accept;
    reject = fail;
  });
  return { promise, resolve, reject };
}

async function flush() {
  await new Promise<void>((resolve) => setImmediate(resolve));
}

function unsupported(operation: keyof ProviderSettingsClient) {
  return async () => {
    throw new Error(`Unexpected ProviderClient operation: ${operation}`);
  };
}

const defaultClient: ProviderSettingsClient = {
  list: async () => [connection],
  getCredentials: async () => credential,
  getModels: async () => emptyCatalog,
  getModelSettings: async () => [],
  getSignIn: async () => pending,
  getBucketSuggestions: unsupported("getBucketSuggestions"),
  confirmBucketSuggestions: unsupported("confirmBucketSuggestions"),
  createChatGpt: unsupported("createChatGpt"),
  createOpenAI: unsupported("createOpenAI"),
  createCustom: unsupported("createCustom"),
  updateCustom: unsupported("updateCustom"),
  remove: unsupported("remove"),
  setApiKey: unsupported("setApiKey"),
  beginSignIn: unsupported("beginSignIn"),
  cancelSignIn: unsupported("cancelSignIn"),
  disconnect: unsupported("disconnect"),
  refreshModels: unsupported("refreshModels"),
  saveModelSettings: unsupported("saveModelSettings"),
};

function setup(
  overrides: Partial<ProviderSettingsClient> = {},
  ready?: (connectionId: string) => Promise<boolean>,
  refreshPreferences?: () => Promise<void>,
) {
  let notifications = 0;
  const client: ProviderSettingsClient = { ...defaultClient, ...overrides };
  const state = new ProviderSettingsController(
    client,
    () => {
      notifications++;
    },
    ready,
    refreshPreferences,
  );
  return { client, state, notifications: () => notifications };
}

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

void test("names come from endpoint hostnames only when creating or saving", async () => {
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
  state.editAdd({ name: "Ignored", endpoint: "https://custom.example:1234/v1/" });
  await state.create();
  state.editAdd({ route: "openAI" });
  await state.create();
  state.editAdd({ route: "custom", name: "Ignored again" });
  await state.create();
  assert.equal(state.snapshot.connections[0]?.name, "Work");
  await state.save(custom);
  assert.deepEqual(names, [
    "api.openai.com",
    "api.openai.com",
    "custom.example",
    "provider.example",
  ]);
  state.setActivity(false, false);
});

void test("closing preserves a dispatched command and guards it across reopen; keys are cleared", async () => {
  const result = deferred<ProviderCredentialStatus>();
  let calls = 0;
  const { state, notifications } = setup({
    setApiKey: () => {
      calls++;
      return result.promise;
    },
  });
  state.setActivity(true, true);
  await flush();
  state.setApiKey("synthetic-secret");
  const command = state.replaceApiKey(connection);
  assert.equal(state.snapshot.apiKey, "");
  assert.equal(state.snapshot.pending, "replaceApiKey");
  state.setActivity(false, true);
  state.setActivity(true, true);
  state.setApiKey("other-synthetic-secret");
  await state.replaceApiKey(connection);
  assert.equal(calls, 1);
  state.setActivity(false, true);
  assert.equal(state.snapshot.apiKey, "");
  result.resolve({ ...credential, hasCredentials: true, requiresSignIn: false });
  await command;
  assert.equal(state.snapshot.pending, null);
  assert.equal(state.snapshot.credentials.one?.hasCredentials, true);
  assert.equal(notifications(), 1);
});

void test("lost acknowledgement auto-reads but still requires explicit permission for new commands", async () => {
  let creates = 0;
  const { state, notifications } = setup({
    createChatGpt: async () => {
      creates++;
      throw new TypeError("response lost");
    },
  });
  state.setActivity(true, true);
  await flush();
  state.select(null);
  await state.create();
  assert.equal(state.snapshot.uncertain, true);
  assert.equal(state.snapshot.connectionsReloadedAfterUncertainty, true);
  assert.equal(notifications(), 1);
  await state.create();
  assert.equal(creates, 1);
  assert.equal(state.snapshot.uncertain, true);
  assert.deepEqual(state.snapshot.notice, {
    operation: "add",
    outcome: "uncertain",
  });
  state.allowNewCommands();
  assert.equal(state.snapshot.uncertain, false);
  assert.equal(creates, 1);
  state.setActivity(false, false);
});

void test("an HTTP conflict is reported as a rejected command", async () => {
  const { state } = setup({
    refreshModels: async () => {
      throw new RequestError(409, { secret: "never display" });
    },
  });
  state.setActivity(true, true);
  await flush();
  await state.refreshModels(connection);
  assert.equal(state.snapshot.uncertain, false);
  assert.equal(state.snapshot.notice?.outcome, "rejected");
  assert.equal(state.snapshot.notice?.httpStatus, 409);
  state.setActivity(false, false);
});

void test("a failed individual status read or list read retains records and other readable statuses", async () => {
  const second = { ...connection, id: "two", name: "Other" };
  const { state, client } = setup({
    list: async () => [connection, second],
    getCredentials: async (id) => {
      if (id === "one") throw new TypeError("offline");
      return { ...credential, connectionId: id };
    },
  });
  state.setActivity(true, true);
  await flush();
  assert.equal(state.snapshot.connections.length, 2);
  assert.equal(state.snapshot.credentials.two?.connectionId, "two");
  assert.ok(state.snapshot.errors["credentials:one"]);
  client.list = async () => {
    throw new TypeError("offline");
  };
  await state.read();
  assert.equal(state.snapshot.connections.length, 2);
  assert.ok(state.snapshot.errors.connections);
  state.setActivity(false, false);
});

void test("polling is visible-only, non-overlapping, terminal-bounded, and rereads credentials", async (context) => {
  context.mock.timers.enable({ apis: ["setTimeout"] });
  let statusReads = 0;
  let credentialReads = 0;
  const delayed = deferred<ProviderSignIn>();
  const { state, notifications } = setup({
    beginSignIn: async () => pending,
    getSignIn: async () => {
      statusReads++;
      return statusReads === 1 ? pending : delayed.promise;
    },
    getCredentials: async () => {
      credentialReads++;
      return credential;
    },
  });
  state.setActivity(true, true);
  await flush();
  await state.beginSignIn(connection);
  assert.equal(statusReads, 1);
  context.mock.timers.tick(2000);
  await flush();
  assert.equal(statusReads, 2);
  context.mock.timers.tick(20000);
  await flush();
  assert.equal(statusReads, 2);
  const beforeTerminal = credentialReads;
  delayed.resolve({ ...pending, status: "completed", authorizationUrl: null });
  await flush();
  assert.equal(credentialReads, beforeTerminal + 1);
  assert.equal(notifications(), 2);
  context.mock.timers.tick(20000);
  await flush();
  assert.equal(statusReads, 2);
  state.setActivity(false, false);
});

void test("binding edits, disconnect and removal require confirmation", async () => {
  const custom: ProviderConnection = { ...connection, route: "custom", authentication: "apiKey" };
  let updates = 0;
  let disconnects = 0;
  let removes = 0;
  const { state } = setup({
    list: async () => [custom],
    updateCustom: async (_id, settings) => {
      updates++;
      assert.equal(settings.name, "new.example");
      return { ...custom, ...settings };
    },
    disconnect: async () => {
      disconnects++;
      return {
        connectionId: "one",
        localCredentialsCleared: true,
        remoteRevocationConfirmed: false,
      };
    },
    remove: async () => {
      removes++;
    },
  });
  state.setActivity(true, true);
  await flush();
  state.edit("one", { endpoint: "https://new.example/v1/" });
  await state.save(custom);
  assert.equal(updates, 0);
  assert.equal(state.snapshot.confirmation?.kind, "binding");
  await state.confirm();
  assert.equal(updates, 1);
  state.requestConfirmation("disconnect", custom);
  assert.equal(disconnects, 0);
  await state.confirm();
  assert.equal(disconnects, 1);
  assert.deepEqual(state.snapshot.notice, {
    operation: "disconnect",
    outcome: "succeeded",
    disconnect: {
      connectionId: "one",
      localCredentialsCleared: true,
      remoteRevocationConfirmed: false,
    },
  });
  state.requestConfirmation("remove", custom);
  assert.equal(removes, 0);
  state.dismissConfirmation();
  assert.equal(removes, 0);
  state.setActivity(false, false);
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

const keyConnection = { ...connection, route: "openAI", authentication: "apiKey" } as const;
const connected = { ...credential, hasCredentials: true, requiresSignIn: false };
const suggestions: BucketSuggestions = {
  connection: keyConnection,
  assignments: [{ bucket: "small", modelId: "model/small" }],
};

for (const decision of ["skip", "confirm", "conflict"] as const) {
  void test(`first key waits for catalog success and only posts on explicit ${decision}`, async (t) => {
    const ready = deferred<boolean>();
    const acknowledgement = deferred<void>();
    let status = credential;
    const calls: string[] = [];
    const { state, notifications } = setup(
      {
        list: async () => [keyConnection],
        getCredentials: async () => status,
        setApiKey: async () => (status = connected),
        getBucketSuggestions: async (id) => {
          calls.push(`get:${id}`);
          return suggestions;
        },
        confirmBucketSuggestions: (proposal) => {
          assert.strictEqual(proposal, suggestions);
          calls.push("post");
          return acknowledgement.promise;
        },
      },
      (id) => {
        calls.push(`ready:${id}`);
        return ready.promise;
      },
      async () => {
        calls.push("preferences");
      },
    );
    t.after(() => state.setActivity(false, false));
    state.setActivity(true, true);
    await flush();
    state.setApiKey("synthetic-key");
    await state.replaceApiKey(keyConnection, true);
    assert.deepEqual(calls, ["ready:one"]);
    assert.equal(state.snapshot.bucketSetup?.phase, "loading");
    assert.equal(notifications(), 0);

    ready.resolve(true);
    await flush();
    assert.deepEqual(calls, ["ready:one", "get:one"]);
    assert.deepEqual(state.snapshot.bucketSetup, { phase: "offered", proposal: suggestions });
    if (decision === "skip") {
      state.skipBucketSuggestions();
      assert.equal(state.snapshot.bucketSetup, null);
      await state.confirmBucketSuggestions();
      await state.read();
      assert.deepEqual(calls, ["ready:one", "get:one"]);
    } else {
      const confirming = state.confirmBucketSuggestions();
      assert.equal(state.snapshot.bucketSetup?.phase, "saving");
      await state.confirmBucketSuggestions();
      assert.deepEqual(calls, ["ready:one", "get:one", "post"]);
      if (decision === "confirm") {
        state.setActivity(false, false); // Closing does not cancel an accepted write.
        acknowledgement.resolve();
        await confirming;
        assert.deepEqual(calls, ["ready:one", "get:one", "post", "preferences"]);
        assert.equal(state.snapshot.bucketSetup, null);
      } else {
        acknowledgement.reject(new RequestError(409, null));
        await confirming;
        assert.deepEqual(calls, ["ready:one", "get:one", "post"]);
        assert.deepEqual(state.snapshot.bucketSetup, {
          phase: "finished",
          error: true,
          message: "Suggestions changed. Review your assignments in Model buckets.",
        });
      }
    }
  });
}

void test("an unsuccessful refresh and routine changes never read suggestions", async (t) => {
  let status = credential;
  let reads = 0;
  let readyCalls = 0;
  const { state, notifications } = setup(
    {
      list: async () => [keyConnection],
      getCredentials: async () => status,
      setApiKey: async () => (status = connected),
      getBucketSuggestions: async () => {
        reads++;
        return suggestions;
      },
      refreshModels: async () => emptyCatalog,
    },
    async () => {
      readyCalls++;
      return false;
    },
  );
  t.after(() => state.setActivity(false, false));
  state.setActivity(true, true);
  await flush();
  state.setApiKey("initial");
  await state.replaceApiKey(keyConnection, true);
  assert.equal(state.snapshot.bucketSetup, null);
  assert.equal(readyCalls, 1);
  state.setApiKey("replacement");
  await state.replaceApiKey(keyConnection, true);
  await state.refreshModels(keyConnection);
  assert.equal(notifications(), 2);
  assert.equal(readyCalls, 1);
  assert.equal(reads, 0);
});

for (const invalidation of [
  "close",
  "switch provider",
  "switch tab",
  "binding",
  "authentication",
] as const) {
  void test(`drops delayed setup suggestions on ${invalidation}`, async (t) => {
    const response = deferred<BucketSuggestions>();
    const start = deferred<AbortSignal | undefined>();
    let saved: ProviderConnection = keyConnection;
    let status = credential;
    const { state } = setup(
      {
        list: async () => [saved],
        getCredentials: async () => status,
        setApiKey: async () => (status = connected),
        getBucketSuggestions: (_id, signal) => {
          start.resolve(signal);
          return response.promise;
        },
      },
      async () => true,
    );
    t.after(() => state.setActivity(false, false));
    state.setActivity(true, true);
    await flush();
    state.setApiKey("initial");
    await state.replaceApiKey(keyConnection, true);
    const signal = await start.promise;
    if (invalidation === "close") state.setActivity(false, false);
    if (invalidation === "switch provider") {
      state.select(null);
      state.select(keyConnection.id);
    }
    if (invalidation === "switch tab") {
      state.setActivity(true, true, false);
      state.setActivity(true, true, true);
    }
    if (invalidation === "binding") saved = { ...saved, endpoint: "https://other.example/v1/" };
    if (invalidation === "authentication") status = { ...connected, requiresSignIn: true };
    await state.read();
    assert.equal(signal?.aborted, true);
    response.resolve(suggestions);
    await flush();
    assert.equal(state.snapshot.bucketSetup, null);
  });
}

void test("a no-auth custom provider is ready after creation; empty suggestions are omitted", async (t) => {
  const custom = { ...connection, route: "custom", authentication: "none" } as const;
  const calls: string[] = [];
  let created = false;
  const { state, notifications } = setup(
    {
      list: async () => (created ? [custom] : []),
      createCustom: async () => {
        created = true;
        return custom;
      },
      getBucketSuggestions: async (id) => {
        calls.push(`get:${id}`);
        return { connection: custom, assignments: [] };
      },
    },
    async (id) => {
      calls.push(`ready:${id}`);
      return true;
    },
  );
  t.after(() => state.setActivity(false, false));
  state.setActivity(true, true);
  await flush();
  state.editAdd({ route: "custom", endpoint: custom.endpoint, authentication: "none" });
  await state.create();
  assert.deepEqual(calls, ["ready:one", "get:one"]);
  assert.equal(notifications(), 0);
  assert.equal(state.snapshot.bucketSetup, null);
});

void test("first OAuth sign-in offers buckets after confirmed credentials, not when it begins", async (t) => {
  let status = credential;
  let signIn = pending;
  const calls: string[] = [];
  const proposal = { ...suggestions, connection };
  const { state, notifications } = setup(
    {
      beginSignIn: async () => signIn,
      getSignIn: async () => signIn,
      getCredentials: async () => status,
      getBucketSuggestions: async (id) => {
        calls.push(`get:${id}`);
        return proposal;
      },
    },
    async (id) => {
      calls.push(`ready:${id}`);
      return true;
    },
  );
  t.after(() => state.setActivity(false, false));
  state.setActivity(true, true);
  await flush();
  await state.beginSignIn(connection, true);
  assert.deepEqual(calls, []);
  signIn = { ...pending, status: "completed", authorizationUrl: null };
  status = { ...connected, planAccessGranted: true };
  await state.read();
  assert.deepEqual(calls, ["ready:one", "get:one"]);
  assert.deepEqual(state.snapshot.bucketSetup, { phase: "offered", proposal });
  assert.equal(notifications(), 0);
});
