import assert from "node:assert/strict";
import test from "node:test";
import { beginSubmission, editDraft, newChat } from "../../chat/ChatState.ts";
import { RequestError } from "../../http/RequestError.ts";
import {
  WorkspaceCommands,
  type WorkspaceCommandState,
} from "../../workspace/WorkspaceCommands.ts";
import { removeWorkspaceSessions } from "../../workspace/WorkspaceState.ts";
import { deferred, emptyClient, fixture, sessionId, snapshot } from "./WorkspaceCommandsFixture.ts";

const otherId = "10000000-0000-0000-0000-000000000002";

function setup(client = emptyClient()) {
  const saved = fixture();
  const states: WorkspaceCommandState[] = [];
  let refreshes = 0;
  const commands = new WorkspaceCommands(
    client,
    saved.access,
    (state) => states.push(state),
    () => {},
    () => refreshes++,
  );
  const forget = (id: string) =>
    saved.access.save(removeWorkspaceSessions(saved.read(), new Set([id])));
  return { ...saved, commands, states, forget, refreshes: () => refreshes };
}

void test("close stays pending, blocks duplicates and removes only the acknowledged chat", async () => {
  const client = emptyClient();
  const response = deferred<void>();
  const dispatches: string[] = [];
  client.closeSession = (id) => {
    dispatches.push(id);
    return response.promise;
  };
  const { commands, access, read, durable, states, forget } = setup(client);

  const closing = commands.close(sessionId, "idle", forget);
  assert.equal(commands.status.closing.has(sessionId), true);
  assert.equal(read().activeId, sessionId);
  await commands.close(sessionId, "idle", forget);
  assert.deepEqual(dispatches, [sessionId]);
  const other = editDraft(newChat(null), "keep this draft");
  access.save({
    ...read(),
    tabs: [sessionId, otherId],
    activeId: otherId,
    chats: { ...read().chats, [otherId]: other },
  });
  response.resolve();
  await closing;

  assert.deepEqual(read().tabs, [otherId]);
  assert.equal(read().activeId, otherId);
  assert.equal(durable().chats[sessionId], undefined);
  assert.equal(durable().chats[otherId]?.draft, "keep this draft");
  assert.deepEqual(
    states.map((state) => state.closing.has(sessionId)),
    [true, false],
  );
});

void test("close does not dispatch with a busy status or unresolved input", async () => {
  const client = emptyClient();
  let dispatched = false;
  client.closeSession = async () => {
    dispatched = true;
  };
  const { commands, access, read, forget } = setup(client);
  for (const status of [
    undefined,
    "running",
    "waiting",
    "compacting",
    "fenced",
    "needs-attention",
    "updates-paused",
  ])
    await commands.close(sessionId, status, forget);
  access.updateChat(sessionId, (chat) =>
    beginSubmission(chat, {
      submissionId: otherId,
      text: "in-flight",
      expectedHistoryRevision: 0,
      llmSelection: null,
    }),
  );
  await commands.close(sessionId, "idle", forget);
  assert.equal(dispatched, false);
  assert.equal(read().chats[sessionId]?.pending?.original.text, "in-flight");
});

void test("a faulted chat can close once cleanup is verified even with a recorded execution", async () => {
  const client = emptyClient();
  let closed: string | undefined;
  client.closeSession = async (id) => {
    closed = id;
  };
  const { commands, access, read, forget } = setup(client);
  access.updateChat(sessionId, (chat) => ({ ...chat, knownExecutionId: otherId }));
  await commands.close(sessionId, "needs-attention", forget);
  assert.equal(closed, undefined);
  await commands.close(sessionId, "faulted-can-close", forget);
  assert.equal(closed, sessionId);
  assert.deepEqual(read().chats, {});
});

void test("409 preserves state and refreshes instead of treating busy as closed", async () => {
  const client = emptyClient();
  client.closeSession = async () => {
    throw new RequestError(409, null);
  };
  client.getSnapshot = async () => assert.fail("No lookup needed for a definitive conflict");
  const { commands, read, forget, refreshes } = setup(client);
  const previous = read();
  await commands.close(sessionId, "idle", forget);
  assert.equal(read(), previous);
  assert.match(commands.status.closeError?.message ?? "", /busy/);
  assert.equal(refreshes(), 1);
});

void test("an older server without the close route does not cost the user their draft", async () => {
  const client = emptyClient();
  client.closeSession = async () => {
    throw new RequestError(404, null);
  };
  client.getSnapshot = async () => snapshot();
  const { commands, access, read, forget } = setup(client);
  access.updateChat(sessionId, (chat) => editDraft(chat, "keep me"));
  await commands.close(sessionId, "idle", forget);
  assert.equal(read().chats[sessionId]?.draft, "keep me");
  assert.match(commands.status.closeError?.message ?? "", /still open/);
});

void test("a 404 confirmed by the snapshot removes a stale chat", async () => {
  const client = emptyClient();
  client.closeSession = async () => {
    throw new RequestError(404, null);
  };
  client.getSnapshot = async () => {
    throw new RequestError(404, null);
  };
  const { commands, read, forget } = setup(client);
  await commands.close(sessionId, "idle", forget);
  assert.equal(read().activeId, null);
  assert.deepEqual(read().chats, {});
  assert.equal(commands.status.closeError, null);
});

void test("ambiguous close with a missing snapshot removes the session without repeating POST", async () => {
  const client = emptyClient();
  let posts = 0;
  client.closeSession = async () => {
    posts++;
    throw new Error("connection lost");
  };
  client.getSnapshot = async () => {
    throw new RequestError(404, null);
  };
  const { commands, read, forget } = setup(client);
  await commands.close(sessionId, "idle", forget);
  assert.equal(posts, 1);
  assert.deepEqual(read().chats, {});
  assert.equal(commands.status.closeError, null);
});

void test("an ambiguous close keeps newer drafts if the chat is present or the check fails", async () => {
  for (const canRead of [true, false]) {
    const client = emptyClient();
    const response = deferred<void>();
    let posts = 0;
    client.closeSession = () => {
      posts++;
      return response.promise;
    };
    client.getSnapshot = async () => {
      if (!canRead) throw new RequestError(503, null);
      return snapshot();
    };
    const { commands, access, read, durable, forget } = setup(client);
    const closing = commands.close(sessionId, "idle", forget);
    access.updateChat(sessionId, (chat) => editDraft(chat, "newer draft"));
    response.reject(new Error("connection lost"));
    await closing;
    assert.equal(posts, 1);
    assert.equal(read().activeId, sessionId);
    assert.equal(durable().chats[sessionId]?.draft, "newer draft");
    assert.match(commands.status.closeError?.message ?? "", /Closure could not be confirmed/);
    assert.equal(commands.status.closeError?.message.includes("still open"), canRead);
    assert.equal(commands.status.closing.has(sessionId), false);
  }
});
