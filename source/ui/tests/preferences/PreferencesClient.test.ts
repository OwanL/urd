import assert from "node:assert/strict";
import test from "node:test";
import { RequestError } from "../../http/RequestError.ts";
import { PreferencesClient } from "../../preferences/PreferencesClient.ts";
import { startHttpServer } from "../support/HttpTestServer.ts";

void test("preferences use the browser API contract", { timeout: 5_000 }, async (t) => {
  const saved = {
    workingDirectory: "C:\\work\\project",
    compactionTriggerTokenCount: 4_096,
    modelChoices: {
      tools: { bucket: null, connectionId: "connection-1", modelId: "tool-model" },
      skills: { bucket: "small", connectionId: null, modelId: null },
      instructions: { bucket: "small", connectionId: null, modelId: null },
      title: { bucket: "small", connectionId: null, modelId: null },
      small: [
        { connectionId: "connection-1", modelId: "small-model" },
        { connectionId: "connection-2", modelId: "small-model" },
      ],
      medium: [],
      large: [],
    },
    subagentProviderConnectionIds: ["connection-2"],
    protectedResources: {
      tools: ["bash", "read"],
      skills: ["C:\\work\\project\\skills\\safe\\SKILL.md"],
      instructions: [],
    },
  };
  const resources = {
    tools: [{ id: "bash", name: "bash" }],
    skills: [{ id: "C:\\work\\project\\skills\\safe\\SKILL.md", name: "Safe" }],
    instructions: [{ id: "C:\\work\\project\\AGENTS.md", name: "AGENTS.md" }],
  };
  const server = await startHttpServer((request, response) => {
    response.writeHead(200, { "Content-Type": "application/json" });
    response.end(
      JSON.stringify(
        request.url === "/api/preferences/resources"
          ? resources
          : request.method === "GET"
            ? { ...saved, workingDirectory: null, compactionTriggerTokenCount: null }
            : saved,
      ),
    );
  });
  t.after(() => server.close());

  const client = new PreferencesClient(new URL("/ignored/path", server.origin));
  assert.deepEqual(await client.get(), {
    ...saved,
    workingDirectory: null,
    compactionTriggerTokenCount: null,
  });
  assert.deepEqual(await client.getSelectableResources(), resources);
  assert.deepEqual(await client.setWorkingDirectory(saved.workingDirectory), saved);
  assert.deepEqual(await client.setWorkingDirectory(null), saved);
  assert.deepEqual(await client.setCompactionTriggerTokenCount(4_096), saved);
  assert.deepEqual(await client.setCompactionTriggerTokenCount(null), saved);
  assert.deepEqual(
    await client.setModelChoice("tools", {
      bucket: null,
      connectionId: "connection-1",
      modelId: "tool-model",
    }),
    saved,
  );
  assert.deepEqual(await client.setBucketModels("small", saved.modelChoices.small), saved);
  assert.deepEqual(await client.setBucketModels("medium", []), saved);
  for (const ids of [null, [], ["connection-2"]]) {
    assert.deepEqual(await client.setSubagentProviders(ids), saved);
  }
  assert.deepEqual(
    await client.setProtectedItems("skills", ["C:\\work\\project\\skills\\safe\\SKILL.md"]),
    saved,
  );

  assert.deepEqual(
    server.requests.map(({ method, url, body }) => ({ method, url, body })),
    [
      { method: "GET", url: "/api/preferences", body: "" },
      { method: "GET", url: "/api/preferences/resources", body: "" },
      {
        method: "POST",
        url: "/api/preferences/working-directory",
        body: JSON.stringify({ workingDirectory: saved.workingDirectory }),
      },
      {
        method: "POST",
        url: "/api/preferences/working-directory",
        body: JSON.stringify({ workingDirectory: null }),
      },
      {
        method: "POST",
        url: "/api/preferences/compaction-trigger",
        body: JSON.stringify({ compactionTriggerTokenCount: 4_096 }),
      },
      {
        method: "POST",
        url: "/api/preferences/compaction-trigger",
        body: JSON.stringify({ compactionTriggerTokenCount: null }),
      },
      {
        method: "POST",
        url: "/api/preferences/model-choice",
        body: JSON.stringify({
          purpose: "tools",
          choice: { bucket: null, connectionId: "connection-1", modelId: "tool-model" },
        }),
      },
      {
        method: "POST",
        url: "/api/preferences/model-bucket",
        body: JSON.stringify({
          bucket: "small",
          models: [
            { connectionId: "connection-1", modelId: "small-model" },
            { connectionId: "connection-2", modelId: "small-model" },
          ],
        }),
      },
      {
        method: "POST",
        url: "/api/preferences/model-bucket",
        body: JSON.stringify({ bucket: "medium", models: [] }),
      },
      {
        method: "POST",
        url: "/api/preferences/subagent-providers",
        body: '{"connectionIds":null}',
      },
      { method: "POST", url: "/api/preferences/subagent-providers", body: '{"connectionIds":[]}' },
      {
        method: "POST",
        url: "/api/preferences/subagent-providers",
        body: '{"connectionIds":["connection-2"]}',
      },
      {
        method: "POST",
        url: "/api/preferences/protected-resources",
        body: JSON.stringify({
          category: "skills",
          items: ["C:\\work\\project\\skills\\safe\\SKILL.md"],
        }),
      },
    ],
  );
  for (const request of server.requests) {
    assert.equal(request.headers["x-urd-client"], "browser");
    assert.equal(request.headers.accept, "application/json");
    assert.equal(request.headers.cookie, undefined);
    assert.equal(
      request.headers["content-type"],
      request.method === "POST" ? "application/json" : undefined,
    );
  }
});

void test("preference validation responses retain the provider error envelope", async (t) => {
  const failure = { error: "Working directory must be an absolute path." };
  const server = await startHttpServer((_request, response) => {
    response.writeHead(400, { "Content-Type": "application/json" });
    response.end(JSON.stringify(failure));
  });
  t.after(() => server.close());

  await assert.rejects(
    new PreferencesClient(server.origin).setWorkingDirectory("relative"),
    (error) => {
      assert.ok(error instanceof RequestError);
      assert.equal(error.status, 400);
      assert.deepEqual(error.responseBody, failure);
      return true;
    },
  );
});
