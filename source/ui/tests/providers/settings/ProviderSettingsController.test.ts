import assert from "node:assert/strict";
import test from "node:test";
import { RequestError } from "../../../http/RequestError.ts";
import type { ProviderConnection } from "../../../providers/ProviderConnection.ts";
import type { ProviderCredentialStatus } from "../../../providers/ProviderCredentials.ts";
import type { ProviderSignIn } from "../../../providers/ProviderSignIn.ts";
import {
  connection,
  credential,
  deferred,
  flush,
  pending,
  setup,
} from "./ProviderSettingsFixtures.ts";

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
