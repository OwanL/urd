import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test, { type TestContext } from "node:test";
import { setTimeout as delay } from "node:timers/promises";
import { RequestError } from "../../http/RequestError.ts";
import { requestJson } from "../../http/Requests.ts";
import { PreferencesClient } from "../../preferences/PreferencesClient.ts";
import { SessionClient } from "../../sessions/SessionClient.ts";
import { ServerProcess } from "../support/ServerProcess.ts";
import { startLocalModelServer, type ModelRequest } from "../support/LocalModelServer.ts";

// Each conversation has its own provider queue; prepasses and titles use separate providers.
async function setup(t: TestContext) {
  const first = await startLocalModelServer();
  t.after(() => first.close());
  const second = await startLocalModelServer();
  t.after(() => second.close());
  const auxiliary = await startLocalModelServer();
  t.after(() => auxiliary.close());
  const titles = await startLocalModelServer();
  t.after(() => titles.close());
  const server = await ServerProcess.start();
  t.after(() => server.stop());
  const directory = await mkdtemp(join(tmpdir(), "urd-agent-communication-"));
  t.after(() => rm(directory, { recursive: true, force: true }));

  const client = new SessionClient(server.origin);
  const preferences = new PreferencesClient(server.origin);
  const firstConnection = await first.install(server.origin);
  const secondConnection = await second.install(server.origin);
  const auxiliaryConnection = await auxiliary.install(server.origin);
  const titleConnection = await titles.install(server.origin);
  for (const purpose of ["tools", "skills", "instructions"] as const)
    await preferences.setModelChoice(purpose, {
      bucket: null,
      connectionId: auxiliaryConnection.id,
      modelId: "fixture-a",
    });
  await preferences.setModelChoice("title", {
    bucket: null,
    connectionId: titleConnection.id,
    modelId: "fixture-a",
  });
  await preferences.setBucketModels("small", [
    { connectionId: secondConnection.id, modelId: "fixture-a" },
  ]);

  const signal = AbortSignal.timeout(45_000);
  async function poll<T>(read: () => Promise<T>, ready: (value: T) => boolean): Promise<T> {
    while (true) {
      signal.throwIfAborted();
      const value = await read();
      if (ready(value)) return value;
      await delay(20, undefined, { signal });
    }
  }
  const questionUrl = (id: string, suffix = "") =>
    new URL(`/api/sessions/${encodeURIComponent(id)}/user-questions${suffix}`, server.origin);
  const questions = (id: string) =>
    requestJson<UserQuestion[]>(questionUrl(id), "GET", undefined, signal);
  const answer = (id: string, questionId: string, value: unknown) =>
    requestJson<{ answer: string }>(
      questionUrl(id, `/${encodeURIComponent(questionId)}/answer`),
      "POST",
      { answer: value },
      signal,
    );
  const pending = async (id: string) => {
    const items = await poll(
      () => questions(id),
      (value) => value.length > 0,
    );
    assert.equal(items.length, 1);
    return items[0]!;
  };
  const snapshot = (id: string) => client.getSnapshot(id, signal);
  const settle = async (id: string, executionId: string) => {
    const result = await poll(
      () => snapshot(id),
      (value) => value.lastSettlement?.executionId === executionId,
    );
    assert.deepEqual(result.lastSettlement, { executionId, outcome: "completed", error: null });
  };
  const create = async () => {
    const id = await client.create();
    await client.setWorkingDirectory(id, directory);
    return id;
  };
  const submit = async (id: string, connection: typeof firstConnection, text: string) => {
    const receipt = await client.submitMessage(id, {
      submissionId: randomUUID(),
      expectedHistoryRevision: 0,
      text,
      llmSelection: {
        connection,
        modelId: "fixture-a",
        family: "other",
        supportsImages: false,
        contextWindow: null,
        reasoningEffort: "high",
      },
    });
    assert.ok(receipt.executionId);
    return receipt.executionId;
  };
  const transcript = async (
    id: string,
    callId: string,
    name: string,
    input: object,
    output: string,
  ) => {
    const history = await client.getHistory(
      id,
      { mode: "latest", expectedHistoryRevision: 0 },
      signal,
    );
    const entry = history.entries.find((candidate) => candidate.tool?.callId === callId);
    assert.ok(entry?.tool);
    assert.equal(entry.tool.name, name);
    assert.equal(entry.tool.outcome, "completed");
    const result = await client.readToolCallTranscript(
      id,
      entry.tool.requestId,
      callId,
      {
        expectedHistoryRevision: entry.entry.historyRevision,
        returned: false,
        full: false,
      },
      signal,
    );
    assert.ok(result);
    assert.equal(result.truncated, false);
    assert.deepEqual(JSON.parse(result.input), input);
    assert.equal(result.output, output);
  };
  return {
    first,
    second,
    titles,
    firstConnection,
    secondConnection,
    client,
    questions,
    answer,
    pending,
    signal,
    poll,
    snapshot,
    settle,
    create,
    submit,
    transcript,
  };
}

type UserQuestion = {
  id: string;
  question: string;
  context: string | null;
  answers: string[];
  sourceTitle: string | null;
  background: boolean;
};

async function rejectsStatus(work: Promise<unknown>, status: number) {
  await assert.rejects(work, (error: unknown) => {
    assert.ok(error instanceof RequestError);
    assert.equal(error.status, status);
    return true;
  });
}

type WireMessage = {
  role?: string;
  content?: unknown;
  tool_call_id?: string;
  tool_calls?: { id?: string; function?: { name?: string } }[];
};

function messages(request: ModelRequest): WireMessage[] {
  return request.messages.filter(
    (message): message is WireMessage => typeof message === "object" && message !== null,
  );
}

function text(message: WireMessage): string {
  if (typeof message.content === "string") return message.content;
  if (Array.isArray(message.content))
    return message.content
      .map((part) =>
        typeof part === "object" && part !== null && "text" in part
          ? String((part as Record<string, unknown>).text)
          : JSON.stringify(part),
      )
      .join("");
  return JSON.stringify(message.content ?? null);
}

function assertToolResult(request: ModelRequest, name: string, callId: string, output: string) {
  const calls = messages(request)
    .flatMap((message) => message.tool_calls ?? [])
    .filter((call) => call.function?.name === name && call.id === callId);
  assert.equal(calls.length, 1);
  const results = messages(request).filter(
    (message) => message.role === "tool" && message.tool_call_id === callId,
  );
  assert.equal(results.length, 1);
  assert.equal(text(results[0]!), output);
}

void test(
  "a foreground subagent returns its report to the primary",
  { timeout: 90_000 },
  async (t) => {
    const f = await setup(t);
    const task = "Report the fixture answer for the delegation scenario.";
    const report = "The fixture answer is 41 — café 東京 verified.";
    const args = { bucket: "small", context: "none", task };
    f.first.enqueue({
      text: "",
      toolCall: { id: "call-subagent", name: "subagent", arguments: args },
    });
    f.first.enqueue({ text: "Relayed the subagent report." });
    f.second.enqueue({ text: report });
    const id = await f.create();
    const execution = await f.submit(id, f.firstConnection, "Delegate the fixture answer lookup.");
    await f.settle(id, execution);

    assert.equal(f.second.calls.length, 1);
    const childUsers = messages(f.second.calls[0]!.request).filter(
      (message) => message.role === "user",
    );
    assert.equal(childUsers.length, 1);
    assert.equal(text(childUsers[0]!), task);
    const listing = await f.client.list(undefined, f.signal);
    assert.deepEqual(listing.sessionIds, [id]);
    assert.equal(listing.nextCursor, null);
    assert.equal(listing.latestLlmSelection?.connection.id, f.firstConnection.id);
    assert.equal(f.first.calls.length, 2);
    assertToolResult(f.first.calls[1]!.request, "subagent", "call-subagent", report);
    await f.transcript(id, "call-subagent", "subagent", args, report);
  },
);

void test(
  "a waking question delivers the sender's prompt and answer",
  { timeout: 90_000 },
  async (t) => {
    const f = await setup(t);
    const recipientTitle = "Fixture Recipient";
    const askerTitle = "Fixture Asker";
    const prompt = "What is the fixture answer for the broker scenario?";
    const answer = "The fixture answer is 42 — café verified.";

    f.titles.enqueue({ text: recipientTitle });
    f.first.enqueue({ text: "Recipient standing by." });
    const recipient = await f.create();
    const recipientBoot = await f.submit(recipient, f.firstConnection, "Stand by for questions.");
    await f.settle(recipient, recipientBoot);
    await f.poll(
      () => f.snapshot(recipient),
      (value) => value.title === recipientTitle,
    );

    f.titles.enqueue({ text: askerTitle });
    f.second.enqueue({ text: "Asker standing by." });
    const asker = await f.create();
    const askerBoot = await f.submit(asker, f.secondConnection, "Stand by for questions.");
    await f.settle(asker, askerBoot);
    await f.poll(
      () => f.snapshot(asker),
      (value) => value.title === askerTitle,
    );

    f.first.enqueue({
      text: "",
      toolCall: {
        id: "call-answer",
        name: "answer_agent",
        arguments: { answer },
      },
    });
    f.first.enqueue({ text: "Answer delivered." });
    const args = { recipient: recipientTitle, prompt, wake: true };
    f.second.enqueue({
      text: "",
      toolCall: { id: "call-ask", name: "ask_agent", arguments: args },
    });
    f.second.enqueue({ text: "Answer relayed." });
    const execution = await f.submit(
      asker,
      f.secondConnection,
      "Ask the recipient for the answer.",
    );
    const woken = await f.poll(
      () => f.snapshot(recipient),
      (value) =>
        value.unsettledExecutionId !== null ||
        (value.lastSettlement !== null && value.lastSettlement.executionId !== recipientBoot),
    );
    const wakeId = woken.unsettledExecutionId ?? woken.lastSettlement?.executionId;
    assert.ok(wakeId);
    await f.settle(recipient, wakeId);
    await f.settle(asker, execution);

    assert.equal(f.first.calls.length, 3);
    assert.equal(f.second.calls.length, 3);
    const question = messages(f.first.calls[1]!.request).find((message) =>
      text(message).includes(prompt),
    );
    assert.ok(question);
    assert.ok(text(question).includes('[QUESTION FROM agent "Fixture Asker"'));
    assert.ok(text(question).endsWith(prompt));
    const history = await f.client.getHistory(
      recipient,
      { mode: "latest", expectedHistoryRevision: 0 },
      f.signal,
    );
    const questionEntry = history.entries.find(
      (entry) => entry.entry.executionId === wakeId && entry.entry.kind === "user",
    );
    assert.ok(questionEntry);
    assert.equal(questionEntry.agentQuestion?.senderTitle, askerTitle);
    assert.equal(
      history.entries.find((entry) => entry.entry.executionId === recipientBoot)?.agentQuestion,
      null,
    );
    const promptPage = await f.client.getPromptMarkers(
      recipient,
      { expectedHistoryRevision: 0 },
      f.signal,
    );
    assert.deepEqual(
      promptPage.prompts.map((prompt) => prompt.executionId),
      [recipientBoot],
    );
    assertToolResult(
      f.first.calls[2]!.request,
      "answer_agent",
      "call-answer",
      "The answer was delivered.",
    );
    assertToolResult(f.second.calls[2]!.request, "ask_agent", "call-ask", answer);
    await f.transcript(asker, "call-ask", "ask_agent", args, answer);
  },
);

for (const answer of ["Choose café", '{\r\n  "answer": "Custom 東京 🧭"\r\n}']) {
  void test(
    `ask_user returns the verbatim ${answer.startsWith("Choose") ? "preset" : "custom"} answer`,
    { timeout: 90_000 },
    async (t) => {
      const f = await setup(t);
      const args = {
        question: "Which option?",
        context: "Context café",
        answers: ["Choose café", "Other"],
      };
      f.first.enqueue({
        text: "",
        toolCall: { id: "call-user", name: "ask_user", arguments: args },
      });
      f.first.enqueue({ text: "Answered." });
      const id = await f.create();
      const execution = await f.submit(id, f.firstConnection, "Ask me.");
      const question = await f.pending(id);
      assert.deepEqual(question, {
        id: question.id,
        ...args,
        sourceTitle: null,
        background: false,
      });
      assert.ok(question.id);
      await rejectsStatus(f.answer(id, question.id, "  \n "), 400);
      await rejectsStatus(f.answer(id, question.id, 42), 400);
      assert.equal((await f.questions(id)).length, 1);
      assert.deepEqual(await f.answer(id, question.id, answer), { answer });
      await rejectsStatus(f.answer(id, question.id, answer), 409);
      await f.settle(id, execution);
      assert.deepEqual(await f.questions(id), []);
      assertToolResult(f.first.calls[1]!.request, "ask_user", "call-user", answer);
      await f.transcript(id, "call-user", "ask_user", args, answer);
    },
  );
}

void test(
  "background ask_user permits steering before its answer",
  { timeout: 90_000 },
  async (t) => {
    const f = await setup(t);
    const args = { question: "Background question?", answers: [], background: true };
    f.first.enqueue({
      text: "",
      toolCall: { id: "call-background-user", name: "ask_user", arguments: args },
    });
    f.first.enqueue({ text: "Waiting for the answer." });
    f.first.enqueue({ text: "Steering received." });
    f.first.enqueue({ text: "Answer received." });
    const id = await f.create();
    const execution = await f.submit(id, f.firstConnection, "Ask in the background.");
    const question = await f.pending(id);
    assert.deepEqual(question, {
      id: question.id,
      question: args.question,
      context: null,
      answers: [],
      sourceTitle: null,
      background: true,
    });
    await f.poll(
      async () => f.first.calls.length,
      (count) => count >= 2,
    );
    await f.submit(id, f.firstConnection, "Steering before the answer.");
    await f.poll(
      async () => f.first.calls.length,
      (count) => count >= 3,
    );
    assert.ok(
      messages(f.first.calls[2]!.request).some((message) =>
        text(message).includes("Steering before the answer."),
      ),
    );
    assert.equal((await f.questions(id)).length, 1);
    const answer = "Background answer";
    assert.deepEqual(await f.answer(id, question.id, answer), { answer });
    await f.settle(id, execution);
    assert.ok(
      f.first.calls
        .slice(3)
        .some(({ request }) => messages(request).some((message) => text(message).includes(answer))),
    );
  },
);

void test("a child ask_user is routed to its primary session", { timeout: 90_000 }, async (t) => {
  const f = await setup(t);
  f.first.enqueue({
    text: "",
    toolCall: {
      id: "call-child",
      name: "subagent",
      arguments: { bucket: "small", context: "none", task: "Ask the user." },
    },
  });
  f.first.enqueue({ text: "Child finished." });
  f.second.enqueue({
    text: "",
    toolCall: {
      id: "call-child-user",
      name: "ask_user",
      arguments: { question: "Child question?", answers: [] },
    },
  });
  f.second.enqueue({ text: "Child report." });
  const id = await f.create();
  const execution = await f.submit(id, f.firstConnection, "Delegate a question.");
  const question = await f.pending(id);
  assert.equal(question.question, "Child question?");
  assert.equal(question.context, null);
  assert.deepEqual(question.answers, []);
  assert.equal(question.background, false);
  assert.ok(question.sourceTitle);
  assert.deepEqual(await f.answer(id, question.id, "Child answer"), { answer: "Child answer" });
  await f.settle(id, execution);
  assertToolResult(f.second.calls[1]!.request, "ask_user", "call-child-user", "Child answer");
  assert.deepEqual(await f.questions(id), []);
});

void test(
  "Stop removes a pending user question and expires its answer",
  { timeout: 90_000 },
  async (t) => {
    const f = await setup(t);
    f.first.enqueue({
      text: "",
      toolCall: {
        id: "call-stop-user",
        name: "ask_user",
        arguments: { question: "Stop?", answers: [] },
      },
    });
    const id = await f.create();
    const execution = await f.submit(id, f.firstConnection, "Ask and wait.");
    const question = await f.pending(id);
    await f.client.stopExecution(id, execution);
    await f.poll(
      () => f.questions(id),
      (items) => items.length === 0,
    );
    await rejectsStatus(f.answer(id, question.id, "Too late"), 409);
  },
);
