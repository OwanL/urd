import assert from "node:assert/strict";
import test from "node:test";
import { RequestError } from "../../../http/RequestError.ts";
import type { BucketSuggestions } from "../../../providers/ProviderClient.ts";
import type { ProviderConnection } from "../../../providers/ProviderConnection.ts";
import {
  connection,
  credential,
  deferred,
  emptyCatalog,
  flush,
  pending,
  setup,
} from "./ProviderSettingsFixtures.ts";

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
    let created = false;
    const calls: string[] = [];
    const { state, notifications } = setup(
      {
        list: async () => (created ? [keyConnection] : []),
        createOpenAI: async () => {
          created = true;
          return keyConnection;
        },
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
    state.chooseProvider("openAI");
    await state.create();
    state.setApiKey("synthetic-key");
    await state.replaceApiKey(keyConnection, true);
    assert.deepEqual(calls, ["ready:one"]);
    assert.equal(state.snapshot.bucketSetup?.phase, "loading");
    assert.equal(notifications(), 1);

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
    let created = false;
    const { state } = setup(
      {
        list: async () => (created ? [saved] : []),
        createOpenAI: async () => {
          created = true;
          return keyConnection;
        },
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
    state.chooseProvider("openAI");
    await state.create();
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
  state.chooseProvider("custom");
  state.editAdd({ endpoint: custom.endpoint, authentication: "none" });
  await state.create();
  assert.deepEqual(calls, ["ready:one", "get:one"]);
  assert.equal(notifications(), 0);
  assert.equal(state.snapshot.bucketSetup, null);
});

void test("reconnecting an existing provider refreshes its catalog without offering buckets", async (t) => {
  let status = credential;
  let reads = 0;
  let refreshed = 0;
  const { state } = setup(
    {
      list: async () => [keyConnection],
      getCredentials: async () => status,
      setApiKey: async () => (status = connected),
      getBucketSuggestions: async () => {
        reads++;
        return suggestions;
      },
    },
    async () => {
      refreshed++;
      return true;
    },
  );
  t.after(() => state.setActivity(false, false));
  state.setActivity(true, true);
  await flush();
  state.setApiKey("key");
  await state.replaceApiKey(keyConnection, true);
  await flush();
  assert.equal(refreshed, 1);
  assert.equal(reads, 0);
  assert.equal(state.snapshot.bucketSetup, null);
});

void test("provider-step offers can be disabled without suppressing catalog refresh", async (t) => {
  const custom = { ...connection, route: "custom", authentication: "none" } as const;
  let created = false;
  let refreshed = 0;
  let reads = 0;
  const { state } = setup(
    {
      list: async () => (created ? [custom] : []),
      createCustom: async () => {
        created = true;
        return custom;
      },
      getBucketSuggestions: async () => {
        reads++;
        return { connection: custom, assignments: [] };
      },
    },
    async () => {
      refreshed++;
      return true;
    },
    undefined,
    false,
  );
  t.after(() => state.setActivity(false, false));
  state.setActivity(true, true);
  await flush();
  state.chooseProvider("custom");
  state.editAdd({ authentication: "none" });
  await state.create();
  await flush();
  assert.equal(refreshed, 1);
  assert.equal(reads, 0);
  assert.equal(state.snapshot.bucketSetup, null);
});

void test("first OAuth sign-in offers buckets after confirmed credentials, not when it begins", async (t) => {
  let status = credential;
  let signIn = pending;
  let created = false;
  const calls: string[] = [];
  const proposal = { ...suggestions, connection };
  const { state, notifications } = setup(
    {
      list: async () => (created ? [connection] : []),
      createChatGpt: async () => {
        created = true;
        return connection;
      },
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
  await state.create();
  await state.beginSignIn(connection, true);
  assert.deepEqual(calls, []);
  signIn = { ...pending, status: "completed", authorizationUrl: null };
  status = { ...connected, planAccessGranted: true };
  await state.read();
  assert.deepEqual(calls, ["ready:one", "get:one"]);
  assert.deepEqual(state.snapshot.bucketSetup, { phase: "offered", proposal });
  assert.equal(notifications(), 1);
});
