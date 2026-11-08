import assert from "node:assert/strict";
import test from "node:test";
import { RequestError } from "../http/RequestError.ts";
import { ProviderClient } from "../providers/ProviderClient.ts";
import type { ProviderConnection } from "../providers/ProviderConnection.ts";
import type { ProviderCredentialStatus } from "../providers/ProviderCredentials.ts";
import type { ProviderSignIn } from "../providers/ProviderSignIn.ts";
import {
  credentialSummary,
  matchesConnection,
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
  assert.equal(state.snapshot.pending, "Replace API key");
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

void test("lost create acknowledgement is uncertain, never replayed, and requires explicit read review", async () => {
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
  state.editAdd({ name: "New" });
  await state.create();
  assert.equal(state.snapshot.uncertain, true);
  assert.equal(notifications(), 1);
  state.setActivity(false, true);
  state.setActivity(true, true);
  await flush();
  await state.create();
  assert.equal(creates, 1);
  assert.equal(state.snapshot.uncertain, true);
  assert.match(state.snapshot.notice ?? "", /outcome uncertain/);
  await state.read();
  assert.equal(state.snapshot.reviewed, true);
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
      rename: async () => {
        throw new RequestError(status, { secret: "never display" });
      },
    });
    state.setActivity(true, true);
    await flush();
    state.edit("one", { name: "Renamed" });
    await state.save(connection);
    assert.equal(state.snapshot.uncertain, uncertain);
    assert.match(state.snapshot.notice ?? "", new RegExp(`HTTP ${status}`));
    assert.doesNotMatch(state.snapshot.notice ?? "", /never display/);
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
  let renames = 0;
  const { state } = setup({
    beginSignIn: async () => pending,
    getSignIn: async () => {
      reads++;
      return pending;
    },
    rename: async () => {
      renames++;
      throw new RequestError(409, undefined);
    },
  });
  state.setActivity(true, true);
  await flush();
  await state.beginSignIn(connection);
  await state.save(connection);
  context.mock.timers.tick(2000);
  await flush();
  assert.equal(reads, 2);
  assert.equal(renames, 1);
  state.setActivity(false, false);
});

void test("hiding aborts pending poll reads and does not poll without a known pending attempt", async (context) => {
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

void test("failed polling stops until an explicit status read; it does not erase known sign-in", async (context) => {
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
  context.mock.timers.tick(10000);
  await flush();
  assert.equal(reads, 1);
  assert.equal(state.snapshot.signIns.one?.status, "pending");
  client.getSignIn = async () => ({ ...pending, status: "expired" });
  await state.read();
  assert.equal(state.snapshot.signIns.one?.status, "expired");
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
    assert.match(
      state.snapshot.notice ?? "",
      remoteRevocationConfirmed === false ? /not confirmed/ : /not applicable/,
    );
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
    rename: async () => {
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
  assert.match(state.snapshot.notice ?? "", /Remote revocation: not confirmed/);
  await state.save(connection);
  assert.equal(state.snapshot.uncertain, true);
  delayed.resolve([connection]);
  await disconnect;
  assert.match(state.snapshot.notice ?? "", /Rename connection: outcome uncertain/);
  state.setActivity(false, false);
});

void test("model discovery is explicit and status text does not imply model entitlement", async () => {
  let refreshes = 0;
  const { state } = setup({
    refreshModels: async () => {
      refreshes++;
      return emptyCatalog;
    },
  });
  state.setActivity(true, true);
  await flush();
  await state.read();
  assert.equal(refreshes, 0);
  await state.refreshModels(connection);
  assert.equal(refreshes, 1);
  assert.match(credentialSummary(connection, undefined), /unknown/);
  assert.equal(credentialSummary(connection, credential), "No credentials");
  assert.match(
    credentialSummary(connection, { ...credential, isDisconnecting: true }),
    /requests blocked/,
  );
  assert.equal(
    credentialSummary(connection, {
      ...credential,
      hasCredentials: true,
      requiresSignIn: false,
      planAccessGranted: false,
    }),
    "Plan access missing",
  );
  assert.equal(
    credentialSummary(connection, { ...credential, hasCredentials: true, requiresSignIn: false }),
    "Connected",
  );
  assert.equal(matchesConnection(connection, "CHATGPT"), true);
  assert.equal(matchesConnection(connection, "provider.example"), true);
  assert.equal(matchesConnection(connection, "secret"), false);
  state.setActivity(false, false);
});
