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
