import assert from "node:assert/strict";
import test from "node:test";
import { RequestError } from "../../http/RequestError.ts";
import type { McpOperation } from "../../mcp/McpServer.ts";
import { connecting, deferred, flush, savedServer, setup } from "./McpSettingsFixtures.ts";

const emptyForm = { url: "", authentication: "automatic", headerName: "", credential: "" };

void test("connect validates the form before any request and reports the failing field", async () => {
  const messages = {
    url: "Enter a valid HTTP or HTTPS URL.",
    headerName: "Enter a header name.",
    credential: "Enter a credential.",
  } as const;
  const cases = [
    [{ url: "not a url", authentication: "automatic", headerName: "", credential: "" }, "url"],
    [
      { url: "ftp://mcp.example", authentication: "automatic", headerName: "", credential: "" },
      "url",
    ],
    [
      {
        url: "https://mcp.example",
        authentication: "customHeader",
        headerName: "   ",
        credential: "",
      },
      "headerName",
    ],
    [
      {
        url: "https://mcp.example",
        authentication: "bearerToken",
        headerName: "",
        credential: " ",
      },
      "credential",
    ],
  ] as const;
  for (const [form, problem] of cases) {
    const { state, notifications } = setup();
    state.setActivity(true);
    await flush();
    state.editForm(form);
    assert.equal(state.connect(), problem);
    assert.equal(state.snapshot.error, messages[problem]);
    assert.equal(state.snapshot.busy, false);
    assert.equal(state.snapshot.operationId, null);
    assert.equal(notifications(), 3); // read, form edit, validation error
  }
});

void test("starting a connection trims the form on the wire and polls until connected", async (t) => {
  t.mock.timers.enable({ apis: ["setTimeout"] });
  const connect = deferred<{ id: string }>();
  const polls = [deferred<McpOperation>(), deferred<McpOperation>()];
  const requests: unknown[] = [];
  let reads = 0;
  let pollCount = 0;
  const { state } = setup({
    list: async () => {
      reads++;
      return [];
    },
    connect: (request) => {
      requests.push(request);
      return connect.promise;
    },
    getOperation: () => polls[pollCount++]!.promise,
  });
  state.setActivity(true);
  await flush();
  assert.equal(state.snapshot.loadError, "");
  assert.deepEqual(state.snapshot.servers, []);

  state.editForm({
    url: " https://mcp.example/v1 ",
    authentication: "automatic",
    credential: "unused",
  });
  assert.equal(state.connect(), null);
  assert.deepEqual(requests, [
    {
      url: "https://mcp.example/v1",
      authentication: "automatic",
      headerName: null,
      credential: null,
    },
  ]);
  assert.equal(state.snapshot.busy, true);

  connect.resolve({ id: "op" });
  await flush();
  assert.equal(state.snapshot.busy, false);
  assert.equal(state.snapshot.operationId, "op");
  assert.equal(state.snapshot.operation, null);

  await flush(); // status check in flight
  polls[0]!.resolve({
    state: "signIn",
    authorizationUrl: "https://auth.example/authorize",
    message: null,
  });
  await flush();
  assert.deepEqual(state.snapshot.operation, {
    state: "signIn",
    authorizationUrl: "https://auth.example/authorize",
    message: null,
  });

  t.mock.timers.tick(1_000);
  await flush(); // next status check in flight
  polls[1]!.resolve({ state: "connected", authorizationUrl: null, message: null });
  await flush();
  assert.equal(state.snapshot.operationId, null);
  assert.equal(state.snapshot.operation, null);
  assert.equal(state.snapshot.screen, "list");
  assert.deepEqual(state.snapshot.form, emptyForm);
  assert.equal(reads, 2); // the server list reloads after acceptance

  t.mock.timers.tick(1_000);
  await flush();
  assert.equal(reads, 2); // tracking does not continue after acceptance
});

void test("commands are refused while an operation is tracked or while one runs", async () => {
  const calls: string[] = [];
  const firstConnect = deferred<{ id: string }>();
  const secondConnect = deferred<{ id: string }>();
  let connects = 0;
  const { state } = setup({
    connect: () => {
      calls.push("connect");
      return connects++ === 0 ? firstConnect.promise : secondConnect.promise;
    },
    reconnect: () => {
      calls.push("reconnect");
      return Promise.resolve({ id: "next" });
    },
    remove: () => {
      calls.push("remove");
      return Promise.resolve();
    },
    cancelOperation: () => {
      calls.push("cancel");
      return Promise.resolve();
    },
    getOperation: () => deferred<McpOperation>().promise,
  });
  state.setActivity(true);
  await flush();
  state.editForm({ url: "https://mcp.example" });

  assert.equal(state.connect(), null);
  firstConnect.resolve({ id: "op" });
  await flush();
  state.reconnect("two");
  await state.remove("one");
  assert.ok(!calls.includes("reconnect"));
  assert.ok(!calls.includes("remove"));

  await state.cancel();
  assert.deepEqual(calls, ["connect", "cancel"]);
  assert.equal(state.snapshot.operationId, null);

  assert.equal(state.connect(), null);
  assert.equal(state.snapshot.busy, true);
  state.reconnect("three");
  await state.remove("one");
  assert.deepEqual(calls, ["connect", "cancel", "connect"]);
});

void test("a vanished operation keeps a failed record with reload and stops polling", async (t) => {
  t.mock.timers.enable({ apis: ["setTimeout"] });
  const connect = deferred<{ id: string }>();
  const operation = deferred<McpOperation>();
  let polls = 0;
  let reads = 0;
  const { state } = setup({
    list: async () => {
      reads++;
      return [savedServer];
    },
    connect: () => connect.promise,
    getOperation: () => {
      polls++;
      return operation.promise;
    },
  });
  state.setActivity(true);
  await flush();
  assert.equal(reads, 1);
  state.editForm({ url: "https://mcp.example", credential: "secret" });
  assert.equal(state.connect(), null);
  connect.resolve({ id: "op" });
  await flush();
  await flush(); // status check in flight
  operation.reject(new RequestError(404, null));
  await flush();
  assert.deepEqual(state.snapshot.operation, {
    state: "failed",
    authorizationUrl: null,
    message: "Connection operation no longer available. Check the server list.",
  });
  assert.equal(state.snapshot.error, "");
  assert.equal(state.snapshot.form.url, "https://mcp.example"); // the draft survives
  assert.equal(state.snapshot.form.credential, "");
  assert.equal(reads, 2);
  t.mock.timers.tick(1_000);
  await flush();
  assert.equal(polls, 1); // no tracking continues for a vanished operation
});

void test("a failed status check is retried after three seconds", async (t) => {
  t.mock.timers.enable({ apis: ["setTimeout"] });
  let polls = 0;
  const { state } = setup({
    reconnect: async () => ({ id: "op" }),
    getOperation: () => {
      polls++;
      if (polls === 1) return Promise.reject(new TypeError("response lost"));
      return Promise.resolve(connecting);
    },
  });
  state.setActivity(true);
  await flush();
  state.reconnect("one");
  await flush();
  await flush();
  assert.equal(polls, 1);
  assert.equal(state.snapshot.error, "Could not check connection: response lost");
  t.mock.timers.tick(2_999);
  await flush();
  assert.equal(polls, 1);
  t.mock.timers.tick(1);
  await flush();
  assert.equal(polls, 2);
  assert.equal(state.snapshot.error, "");
  assert.deepEqual(state.snapshot.operation, connecting);
});

void test("cancelling while connecting settles acknowledged and vanished operations alike", async (t) => {
  t.mock.timers.enable({ apis: ["setTimeout"] });
  for (const outcome of [null, new RequestError(404, null)] as const) {
    const operation = deferred<McpOperation>();
    let polls = 0;
    let reads = 0;
    let cancels = 0;
    const { state } = setup({
      list: async () => {
        reads++;
        return [savedServer];
      },
      connect: async () => ({ id: "op" }),
      cancelOperation: () => {
        cancels++;
        return outcome === null ? Promise.resolve() : Promise.reject(outcome);
      },
      getOperation: () => {
        polls++;
        return operation.promise;
      },
    });
    state.setActivity(true);
    await flush();
    state.editForm({ url: "https://mcp.example", credential: "secret" });
    assert.equal(state.connect(), null);
    await flush(); // status check in flight
    operation.resolve(connecting);
    await flush();
    assert.equal(polls, 1);

    await state.cancel();
    assert.equal(cancels, 1);
    assert.equal(state.snapshot.operationId, null);
    assert.equal(state.snapshot.form.credential, "");
    assert.equal(reads, 2);
    t.mock.timers.tick(1_000);
    await flush();
    assert.equal(polls, 1); // tracking ends with the cancel
  }
});

void test("a reported failure is dismissed without cancelling", async () => {
  let cancels = 0;
  const { state } = setup({
    connect: async () => ({ id: "op" }),
    getOperation: async () => ({
      state: "failed",
      authorizationUrl: null,
      message: "Unable to connect to the MCP server.",
    }),
    cancelOperation: () => {
      cancels++;
      return Promise.resolve();
    },
  });
  state.setActivity(true);
  await flush();
  state.editForm({ url: "https://mcp.example", credential: "secret" });
  assert.equal(state.connect(), null);
  await flush();
  await flush();
  assert.equal(state.snapshot.operation?.state, "failed");
  assert.equal(state.snapshot.form.credential, "secret"); // a failure alone keeps the credential
  await state.cancel();
  assert.equal(cancels, 0);
  assert.equal(state.snapshot.operationId, null);
  assert.equal(state.snapshot.operation, null);
  assert.equal(state.snapshot.form.credential, "");
  assert.equal(state.snapshot.error, "");
});

void test("stop tracking reports the connection status as unknown", async (t) => {
  t.mock.timers.enable({ apis: ["setTimeout"] });
  const operation = deferred<McpOperation>();
  let polls = 0;
  let reads = 0;
  const { state } = setup({
    list: async () => {
      reads++;
      return [savedServer];
    },
    connect: async () => ({ id: "op" }),
    getOperation: () => {
      polls++;
      return operation.promise;
    },
  });
  state.setActivity(true);
  await flush();
  state.editForm({ url: "https://mcp.example", credential: "secret" });
  assert.equal(state.connect(), null);
  await flush();
  await flush();
  state.stopTracking();
  assert.equal(state.snapshot.operationId, null);
  assert.equal(state.snapshot.error, "Connection status unknown. Check the server list.");
  assert.equal(state.snapshot.form.credential, "");
  assert.equal(reads, 2);
  t.mock.timers.tick(1_000);
  await flush();
  assert.equal(polls, 1); // no further tracking once stopped
});

void test("removing a server returns to the list and reloads after success or failure", async () => {
  for (const outcome of [null, new TypeError("response lost")] as const) {
    let reads = 0;
    const { state } = setup({
      list: async () => {
        reads++;
        return [savedServer];
      },
      remove: () => (outcome === null ? Promise.resolve() : Promise.reject(outcome)),
    });
    state.setActivity(true);
    await flush();
    state.select("one");
    state.show("detail");
    await state.remove("one");
    if (outcome === null) {
      assert.equal(state.snapshot.screen, "list");
      assert.equal(state.snapshot.error, "");
      assert.equal(reads, 2);
    } else {
      assert.equal(state.snapshot.screen, "detail");
      assert.equal(state.snapshot.error, "Could not remove server: response lost");
      assert.equal(reads, 1);
    }
    assert.equal(state.snapshot.busy, false);
  }
});

void test("navigation clears the add draft and any validation error while idle", async () => {
  const { state } = setup();
  state.setActivity(true);
  await flush();
  state.editForm({ url: "not a url" });
  assert.equal(state.connect(), "url");
  state.show("list");
  assert.equal(state.snapshot.error, "");

  state.show("add");
  state.editForm({ url: "https://mcp.example", credential: "secret" });
  state.show("list");
  state.show("add"); // entering the add screen always starts an empty draft
  assert.deepEqual(state.snapshot.form, emptyForm);
});

void test("navigation keeps the draft while a connection command runs", async () => {
  const pending = deferred<{ id: string }>();
  const { state } = setup({ connect: () => pending.promise });
  state.setActivity(true);
  await flush();
  state.editForm({ url: "https://mcp.example", credential: "secret" });
  assert.equal(state.connect(), null);
  assert.equal(state.snapshot.busy, true);
  state.show("list");
  state.show("add");
  assert.deepEqual(state.snapshot.form, {
    url: "https://mcp.example",
    authentication: "automatic",
    headerName: "",
    credential: "secret",
  });
});
