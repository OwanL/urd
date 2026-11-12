import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import test from "node:test";
import { setTimeout as delay } from "node:timers/promises";
import { SessionClient } from "../sessions/SessionClient.ts";
import type { UserMessageSubmission } from "../sessions/UserMessageSubmission.ts";
import { RequestError } from "../http/RequestError.ts";
import { ServerProcess } from "./ServerProcess.ts";
import { startLocalModelServer } from "./LocalModelServer.ts";

function beforeDeadline<T>(operation: Promise<T>, deadline: number): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  return Promise.race([
    operation,
    new Promise<never>((_, reject) => {
      timer = setTimeout(
        () => reject(new Error("Session integration deadline exceeded")),
        Math.max(1, deadline - performance.now()),
      );
    }),
  ]).finally(() => clearTimeout(timer));
}

// Node owns top-level test completion; only the registration promise is intentionally ignored.
void test(
  "SessionClient preserves the real server's session and message contract",
  { timeout: 45_000 },
  async (t) => {
    const model = await startLocalModelServer();
    t.after(() => model.close());
    const server = await ServerProcess.start();
    t.after(() => server.stop());
    const connection = await model.install(server.origin);
    const client = new SessionClient(server.origin);
    const deadline = performance.now() + 20_000;
    const signal = AbortSignal.timeout(20_000);
    const request = <T>(operation: Promise<T>) => beforeDeadline(operation, deadline);

    const page = await fetch(server.origin, { signal });
    assert.equal(page.status, 200);
    assert.match(page.headers.get("content-type") ?? "", /^text\/html/);
    assert.equal(page.headers.get("cache-control"), "no-store");
    const html = await page.text();
    const script = html.match(/<script\b[^>]*src="(\/assets\/[^\"]+\.js)"/);
    assert.ok(script, "The staged HTML must load the real bundled application");
    const asset = await fetch(new URL(script[1]!, server.origin), { signal });
    assert.equal(asset.status, 200);
    assert.match(asset.headers.get("cache-control") ?? "", /immutable/);
    await asset.arrayBuffer();
    const forbidden = await fetch(server.origin, {
      headers: { Origin: "https://example.invalid" },
      signal,
    });
    assert.equal(forbidden.status, 403);
    await forbidden.arrayBuffer();
    const privateApi = await fetch(new URL("/api/sessions", server.origin), { signal });
    assert.equal(privateApi.status, 403, "Static hosting must not bypass the API marker");
    await privateApi.arrayBuffer();

    assert.deepEqual(await request(client.list(undefined, signal)), {
      sessionIds: [],
      nextCursor: null,
    });
    const id = await request(client.create());
    assert.match(id, /^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i);
    assert.deepEqual(await request(client.list(undefined, signal)), {
      sessionIds: [id],
      nextCursor: null,
    });
    const initial = await request(client.getSnapshot(id, signal));
    assert.deepEqual(initial, {
      sessionId: id,
      historyRevision: 0,
      admissionFenced: false,
      isCompacting: false,
      unsettledExecutionId: null,
      stopRequested: false,
      lastSettlement: null,
      latestLlmSelection: null,
      firstUserPreview: null,
      pendingInputs: [],
      contextUsage: null,
      workingTime: { completedMilliseconds: 0, startedAt: null, hasUnknownDuration: false },
    });

    // Deliberately retain whitespace, CRLF, non-ASCII and astral scalars, well beyond the preview.
    const submission: UserMessageSubmission = {
      submissionId: randomUUID(),
      expectedHistoryRevision: initial.historyRevision,
      llmSelection: {
        connection,
        modelId: "fixture-a",
        family: "other",
        supportsImages: false,
        contextWindow: null,
        reasoningEffort: "high",
      },
      text:
        "\n  Original café 東京 🧭\r\n" + "line: é / 🦉 / 漢字\n".repeat(200) + "  final line\n",
    };
    model.enqueue({ text: "Hello from Urd.", usage: { inputTokens: 12_300, outputTokens: 456 } });
    const receipt = await request(client.submitMessage(id, submission));
    assert.equal(receipt.sessionId, id);
    assert.deepEqual(receipt.submission, submission);
    assert.equal(receipt.acceptance, "accepted");
    assert.equal(receipt.refusalReason, null);
    assert.ok(receipt.executionId);
    const executionId = receipt.executionId;
    assert.deepEqual(
      await request(client.getMessageReceipt(id, submission.submissionId, signal)),
      receipt,
    );
    // Retry the explicit original payload, not a fresh submission or a normalized version of the text.
    assert.deepEqual(await request(client.submitMessage(id, { ...submission })), receipt);

    let settled = await request(client.getSnapshot(id, signal));
    while (settled.lastSettlement?.executionId !== executionId) {
      assert.ok(performance.now() < deadline, "Execution did not settle before the deadline");
      await delay(Math.min(20, Math.max(1, deadline - performance.now())), undefined, { signal });
      settled = await request(client.getSnapshot(id, signal));
    }
    assert.equal(settled.sessionId, id);
    assert.equal(settled.historyRevision, initial.historyRevision);
    assert.equal(settled.admissionFenced, false);
    assert.equal(settled.unsettledExecutionId, null);
    assert.equal(settled.stopRequested, false);
    assert.deepEqual(settled.lastSettlement, { executionId, outcome: "completed", error: null });
    assert.deepEqual(settled.pendingInputs, []);
    assert.deepEqual(settled.latestLlmSelection, submission.llmSelection);
    assert.deepEqual(settled.contextUsage, { usedTokens: 12_756, isEstimate: false });
    assert.ok(settled.workingTime);
    assert.equal(settled.workingTime.startedAt, null);
    assert.equal(settled.workingTime.hasUnknownDuration, false);
    assert.ok(settled.workingTime.completedMilliseconds > 0);
    assert.ok(settled.firstUserPreview);
    assert.ok(
      settled.firstUserPreview.length > 0 &&
        settled.firstUserPreview.length < submission.text.length,
    );
    assert.ok(submission.text.startsWith(settled.firstUserPreview));
    assert.equal(model.calls.length, 1, "An exact retry must not make another provider request");
    assert.equal(model.calls[0]!.request.model, "fixture-a");
    assert.equal(model.calls[0]!.request.reasoning_effort, "high");

    const history = await request(
      client.getHistory(
        id,
        { mode: "latest", expectedHistoryRevision: initial.historyRevision },
        signal,
      ),
    );
    assert.equal(history.sessionId, id);
    assert.equal(history.historyRevision, initial.historyRevision);
    assert.equal(history.hasBefore, false);
    assert.equal(history.hasAfter, false);
    assert.deepEqual(history.settlements, [{ executionId, outcome: "completed", error: null }]);
    const user = history.entries.find((entry) => entry.entry.kind === "user");
    const assistant = history.entries.find((entry) => entry.entry.kind === "text");
    assert.ok(user?.text);
    assert.ok(assistant?.text);
    assert.equal(user.entry.executionId, executionId);
    assert.equal(user.entry.entryId, submission.submissionId);
    assert.equal(user.entry.historyRevision, initial.historyRevision);
    assert.equal(user.text.isClosed, true);
    assert.equal(user.text.finalOutputCommitted, false);

    // Metadata stays in history; full text is read through UTF-8 byte cursors.
    const userContent = await request(client.getMessageContent(id, user.text.from, signal));
    assert.deepEqual(userContent.from, user.text.from);
    assert.equal(userContent.text, submission.text);
    assert.equal(userContent.hasMore, false);
    assert.equal(userContent.isClosed, true);
    assert.equal(userContent.finalOutputCommitted, false);
    assert.deepEqual(userContent.availableThrough, user.text.availableThrough);
    assert.deepEqual(userContent.next, userContent.availableThrough);

    const output = await request(client.getMessageContent(id, assistant.text.from, signal));
    assert.equal(output.text, "Hello from Urd.");
    assert.equal(output.hasMore, false);
    assert.equal(output.isClosed, true);
    assert.equal(output.finalOutputCommitted, true);
    assert.deepEqual(output.next, assistant.text.availableThrough);

    assert.deepEqual(await request(client.stopExecution(id, executionId)), {
      executionId,
      status: "settled",
      outcome: "completed",
    });
    assert.equal(await request(client.getMessageReceipt(id, randomUUID(), signal)), null);
    // Reusing an accepted submission ID with changed text is an HTTP conflict, not a new receipt.
    await assert.rejects(
      request(client.submitMessage(id, { ...submission, text: submission.text + "changed" })),
      (error) => {
        assert.ok(error instanceof RequestError);
        assert.equal(error.status, 409);
        assert.ok("responseBody" in error);
        return true;
      },
    );

    const edit = {
      operationId: randomUUID(),
      message: user.text.from.message,
      text: "Edited instruction: 雪🙂",
      llmSelection: { ...submission.llmSelection!, modelId: "fixture-b", reasoningEffort: "low" },
    };
    const saved = await request(client.editMessage(id, edit));
    assert.equal(saved.historyRevision, 1);
    assert.deepEqual(
      await request(client.getMessageEditReceipt(id, edit.operationId, signal)),
      saved,
    );
    assert.deepEqual(await request(client.editMessage(id, edit)), saved);
    let edited = await request(client.getSnapshot(id, signal));
    while (edited.lastSettlement?.executionId !== saved.executionId) {
      await delay(20, undefined, { signal });
      edited = await request(client.getSnapshot(id, signal));
    }
    assert.equal(model.calls.length, 2, "An edit retry must not start another provider request");
    assert.equal(model.calls[1]!.request.model, "fixture-b");
    assert.equal(model.calls[1]!.request.reasoning_effort, "low");
    const context = JSON.stringify(model.calls[1]!.request.messages);
    assert.ok(context.includes(edit.text));
    assert.ok(!context.includes("Original café"));
    assert.ok(!context.includes("Hello from Urd."));
    await assert.rejects(
      client.getMessageContent(id, user.text.from, signal),
      (error) => error instanceof RequestError && error.status === 409,
    );
    const revised = await request(
      client.getHistory(id, { mode: "latest", expectedHistoryRevision: 1 }, signal),
    );
    assert.equal(revised.entries.length, 2);
    assert.equal(revised.entries[0]!.entry.entryId, edit.operationId);
  },
);
