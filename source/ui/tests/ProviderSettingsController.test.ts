import assert from "node:assert/strict";
import test from "node:test";
import { RequestError } from "../http/RequestError.ts";
import { ProviderClient } from "../providers/ProviderClient.ts";
import type { ProviderConnection } from "../providers/ProviderConnection.ts";
import type { ProviderCredentialStatus } from "../providers/ProviderCredentials.ts";
import type { ProviderSignIn } from "../providers/ProviderSignIn.ts";
import {
  credentialState,
  matchesConnection,
  providerHostname,
  ProviderSettingsController,
} from "../providers/settings/ProviderSettingsController.ts";

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

function setup(overrides: Partial<ProviderClient> = {}) {
  let notifications = 0;
  const client: ProviderClient = Object.assign(
    new ProviderClient(new URL("http://localhost:1")),
    {
      list: async () => [connection],
      getCredentials: async () => credential,
      getModels: async () => emptyCatalog,
      getSignIn: async () => pending,
    },
    overrides,
  );
  const state = new ProviderSettingsController(client, () => {
    notifications++;
  });
  return { client, state, notifications: () => notifications };
}

void test("snapshot is exposed through a read-only controller getter", () => {
  const { state } = setup();
  const snapshot = state.snapshot;
  assert.equal(Reflect.set(state, "snapshot", {}), false);
  assert.equal(state.snapshot, snapshot);
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
  assert.equal(state.snapshot.reviewed, true);
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

void test("only explicit terminal HTTP rejection is called rejected; server failures remain uncertain", async () => {
  for (const [status, uncertain] of [
    [409, false],
    [503, true],
  ] as const) {
    const { state } = setup({
      refreshModels: async () => {
        throw new RequestError(status, { secret: "never display" });
      },
    });
    state.setActivity(true, true);
    await flush();
    await state.refreshModels(connection);
    assert.equal(state.snapshot.uncertain, uncertain);
    assert.equal(state.snapshot.notice?.outcome, uncertain ? "uncertain" : "rejected");
    assert.equal(state.snapshot.notice?.httpStatus, status);
    state.setActivity(false, false);
  }
});

void test("aborted obsolete reads cannot overwrite state, and ordinary reads preserve drafts", async () => {
  const stale = deferred<readonly ProviderConnection[]>();
  let signal: AbortSignal | undefined;
  const { state, client } = setup({
    list: (readSignal) => {
      signal = readSignal;
      return stale.promise;
    },
  });
  state.setActivity(true, true);
  state.setActivity(false, true);
  assert.equal(signal?.aborted, true);
  client.list = async () => [connection];
  state.setActivity(true, true);
  await flush();
  client.list = async () => [{ ...connection, name: "Remote name" }];
  await state.read();
  assert.equal(state.snapshot.drafts.one?.name, "Remote name");
  state.edit("one", { name: "Unsaved name" });
  state.setApiKey("synthetic");
  await state.read();
  assert.equal(state.snapshot.drafts.one?.name, "Unsaved name");
  stale.resolve([{ ...connection, name: "Stale" }]);
  await flush();
  assert.equal(state.snapshot.connections[0]?.name, "Remote name");
  assert.equal(state.snapshot.apiKey, "synthetic");
  state.select(null);
  assert.equal(state.snapshot.apiKey, "");
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

void test("metadata refreshes every ten seconds without overlap or changing edits, and recovers after errors", async (context) => {
  context.mock.timers.enable({ apis: ["setTimeout"] });
  const delayed = deferred<readonly ProviderConnection[]>();
  let lists = 0;
  let credentials = 0;
  let models = 0;
  const { state, client } = setup({
    list: async () => (++lists === 2 ? delayed.promise : [connection]),
    getCredentials: async () => {
      credentials++;
      return credential;
    },
    getModels: async () => {
      models++;
      return emptyCatalog;
    },
  });
  state.setActivity(true, true);
  await flush();
  state.edit("one", { endpoint: "https://edited.example/" });
  state.editAdd({ endpoint: "https://draft.example/" });
  state.setApiKey("synthetic");
  state.requestConfirmation("disconnect", connection);
  context.mock.timers.tick(9999);
  assert.equal(lists, 1);
  context.mock.timers.tick(1);
  await state.read();
  context.mock.timers.tick(30000);
  assert.equal(lists, 2);
  delayed.resolve([connection]);
  await flush();
  assert.equal(credentials, 2);
  assert.equal(models, 2);
  assert.equal(state.snapshot.drafts.one?.endpoint, "https://edited.example/");
  assert.equal(state.snapshot.add.endpoint, "https://draft.example/");
  assert.equal(state.snapshot.apiKey, "synthetic");
  assert.equal(state.snapshot.confirmation?.kind, "disconnect");
  state.setActivity(true, false);
  context.mock.timers.tick(30000);
  assert.equal(lists, 2);
  client.list = async () => {
    throw new TypeError("offline");
  };
  state.setActivity(true, true);
  await flush();
  assert.ok(state.snapshot.errors.connections);
  client.list = async () => [connection];
  context.mock.timers.tick(10000);
  await flush();
  assert.equal(state.snapshot.errors.connections, undefined);
  state.setActivity(false, false);
  const before = credentials;
  context.mock.timers.tick(30000);
  await flush();
  assert.equal(credentials, before);
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

void test("a rejected unrelated command resumes observation without repeating the command", async (context) => {
  context.mock.timers.enable({ apis: ["setTimeout"] });
  let reads = 0;
  let discoveries = 0;
  const { state } = setup({
    beginSignIn: async () => pending,
    getSignIn: async () => {
      reads++;
      return pending;
    },
    refreshModels: async () => {
      discoveries++;
      throw new RequestError(409, undefined);
    },
  });
  state.setActivity(true, true);
  await flush();
  await state.beginSignIn(connection);
  await state.refreshModels(connection);
  context.mock.timers.tick(2000);
  await flush();
  assert.equal(reads, 2);
  assert.equal(discoveries, 1);
  state.setActivity(false, false);
});

void test("hiding aborts pending reads and only known attempts are polled", async (context) => {
  context.mock.timers.enable({ apis: ["setTimeout"] });
  const delayed = deferred<ProviderSignIn>();
  let reads = 0;
  let pollSignal: AbortSignal | undefined;
  const { state } = setup({
    beginSignIn: async () => pending,
    getSignIn: (_id, signal) => {
      reads++;
      if (reads === 1) return Promise.resolve(pending);
      pollSignal = signal;
      return delayed.promise;
    },
  });
  state.setActivity(true, true);
  await flush();
  context.mock.timers.tick(10000);
  await flush();
  assert.equal(reads, 0);
  await state.beginSignIn(connection);
  context.mock.timers.tick(2000);
  await flush();
  state.setActivity(true, false);
  assert.equal(pollSignal?.aborted, true);
  delayed.resolve({ ...pending, status: "completed" });
  await flush();
  assert.equal(state.snapshot.signIns.one?.status, "pending");
  context.mock.timers.tick(20000);
  assert.equal(reads, 2);
  state.setActivity(false, false);
});

void test("read errors slow pending observation to ten seconds and recover automatically", async (context) => {
  context.mock.timers.enable({ apis: ["setTimeout"] });
  let reads = 0;
  const { state, client } = setup({
    beginSignIn: async () => pending,
    getSignIn: async () => {
      reads++;
      throw new TypeError("unavailable");
    },
  });
  state.setActivity(true, true);
  await flush();
  await state.beginSignIn(connection);
  context.mock.timers.tick(2000);
  await flush();
  assert.equal(reads, 1);
  assert.equal(state.snapshot.signIns.one?.status, "pending");
  client.getSignIn = async () => {
    reads++;
    return pending;
  };
  context.mock.timers.tick(7999);
  await flush();
  assert.equal(reads, 1);
  context.mock.timers.tick(1);
  await flush();
  assert.equal(reads, 2);
  assert.equal(state.snapshot.errors["signIn:one"], undefined);
  context.mock.timers.tick(2000);
  await flush();
  assert.equal(reads, 3);
  state.setActivity(false, false);
});

void test("a missing sign-in after server restart permits an explicit fresh sign-in", async () => {
  let starts = 0;
  const { state } = setup({
    beginSignIn: async () => ({ ...pending, id: `attempt-${++starts}` }),
    getSignIn: async () => {
      if (starts === 1) throw new RequestError(404, undefined);
      return { ...pending, id: `attempt-${starts}` };
    },
  });
  state.setActivity(true, true);
  await flush();
  await state.beginSignIn(connection);
  assert.equal(Object.hasOwn(state.snapshot.signIns, "one"), false);
  await state.beginSignIn(connection);
  assert.equal(starts, 2);
  assert.equal(state.snapshot.signIns.one?.id, "attempt-2");
  state.setActivity(false, false);
});

void test("binding edits, disconnect and removal require confirmation; remote revocation is not invented", async () => {
  const custom: ProviderConnection = { ...connection, route: "custom", authentication: "apiKey" };
  for (const remoteRevocationConfirmed of [false, null] as const) {
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
        return { connectionId: "one", localCredentialsCleared: true, remoteRevocationConfirmed };
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
        remoteRevocationConfirmed,
      },
    });
    state.requestConfirmation("remove", custom);
    assert.equal(removes, 0);
    state.dismissConfirmation();
    assert.equal(removes, 0);
    state.setActivity(false, false);
  }
});

void test("an older disconnect reconciliation cannot overwrite a newer uncertain command notice", async () => {
  const delayed = deferred<readonly ProviderConnection[]>();
  const { state, client } = setup({
    disconnect: async () => ({
      connectionId: "one",
      localCredentialsCleared: true,
      remoteRevocationConfirmed: false,
    }),
    refreshModels: async () => {
      throw new TypeError("acknowledgement lost");
    },
  });
  state.setActivity(true, true);
  await flush();
  client.list = () => delayed.promise;
  state.requestConfirmation("disconnect", connection);
  const disconnect = state.confirm();
  await flush();
  assert.equal(state.snapshot.pending, null);
  assert.deepEqual(state.snapshot.notice, {
    operation: "disconnect",
    outcome: "succeeded",
    disconnect: {
      connectionId: "one",
      localCredentialsCleared: true,
      remoteRevocationConfirmed: false,
    },
  });
  const discovery = state.refreshModels(connection);
  await flush();
  assert.equal(state.snapshot.uncertain, true);
  delayed.resolve([connection]);
  await Promise.all([disconnect, discovery]);
  assert.equal(state.snapshot.notice?.operation, "refreshModels");
  assert.equal(state.snapshot.notice?.outcome, "uncertain");
  state.setActivity(false, false);
});

void test("model discovery is explicit and status text does not imply model entitlement", async (context) => {
  context.mock.timers.enable({ apis: ["setTimeout"] });
  let refreshes = 0;
  const { state } = setup({
    refreshModels: async () => {
      refreshes++;
      return emptyCatalog;
    },
  });
  state.setActivity(true, true);
  await flush();
  context.mock.timers.tick(10000);
  await flush();
  assert.equal(refreshes, 0);
  await state.refreshModels(connection);
  assert.equal(refreshes, 1);
  assert.equal(credentialState(connection, undefined), "unknown");
  assert.equal(
    credentialState({ ...connection, authentication: "none" }, credential),
    "noAuthentication",
  );
  assert.equal(credentialState(connection, credential), "noCredentials");
  assert.equal(
    credentialState(connection, { ...credential, hasCredentials: true, requiresSignIn: true }),
    "signInRequired",
  );
  assert.equal(
    credentialState(connection, { ...credential, isDisconnecting: true }),
    "disconnecting",
  );
  assert.equal(
    credentialState(connection, {
      ...credential,
      hasCredentials: true,
      requiresSignIn: false,
      planAccessGranted: false,
    }),
    "planAccessMissing",
  );
  assert.equal(
    credentialState(connection, { ...credential, hasCredentials: true, requiresSignIn: false }),
    "connected",
  );
  assert.equal(matchesConnection(connection, "CHATGPT"), true);
  assert.equal(matchesConnection(connection, "provider.example"), true);
  assert.equal(matchesConnection(connection, "secret"), false);
  state.setActivity(false, false);
});
