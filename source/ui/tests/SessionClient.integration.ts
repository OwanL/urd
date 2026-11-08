import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import test from "node:test";
import { setTimeout as delay } from "node:timers/promises";
import { SessionClient } from "../sessions/SessionClient.ts";
import { SessionRequestError } from "../sessions/SessionRequestError.ts";
import { ServerProcess } from "./ServerProcess.ts";

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
    const server = await ServerProcess.start();
    t.after(() => server.stop());
    const client = new SessionClient(server.origin);
    const deadline = performance.now() + 20_000;
    const signal = AbortSignal.timeout(20_000);
    const request = <T>(operation: Promise<T>) => beforeDeadline(operation, deadline);

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
      unsettledExecutionId: null,
      stopRequested: false,
      lastSettlement: null,
      messages: [],
      hasOlderMessages: false,
    });

    // Deliberately retain whitespace, CRLF, non-ASCII and astral scalars, well beyond the preview.
    const submission = {
      submissionId: randomUUID(),
      expectedHistoryRevision: initial.historyRevision,
      text:
        "\n  Original café 東京 🧭\r\n" + "line: é / 🦉 / 漢字\n".repeat(200) + "  final line\n",
    };
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
    assert.deepEqual(settled.lastSettlement, { executionId, outcome: "completed" });
    assert.equal(settled.hasOlderMessages, false);
    assert.equal(settled.messages.length, 2);
    const user = settled.messages.find((message) => message.message.role === "user");
    const assistant = settled.messages.find((message) => message.message.role === "assistant");
    assert.ok(user);
    assert.ok(assistant);
    assert.equal(user.message.executionId, executionId);
    assert.equal(user.message.messageId, submission.submissionId);
    assert.equal(user.message.historyRevision, initial.historyRevision);
    assert.equal(user.truncated, true);
    assert.ok(user.preview.length > 0 && user.preview.length < submission.text.length);
    assert.ok(submission.text.startsWith(user.preview));
    assert.equal(user.settlement, "completed");
    assert.equal(assistant.settlement, "completed");
    assert.equal(assistant.finalOutputCommitted, true);

    // Cursors contain a nested MessageRef; offsets are UTF-8 bytes, not JS string lengths.
    const from = {
      message: user.message,
      segmentIndex: 0,
      utf8Offset: Buffer.byteLength(user.preview, "utf8"),
    };
    const remainder = await request(client.getMessageContent(id, from, signal));
    assert.deepEqual(remainder.from, from);
    assert.equal(user.preview + remainder.text, submission.text);
    assert.equal(remainder.hasMore, false);
    assert.equal(remainder.isClosed, true);
    assert.equal(remainder.finalOutputCommitted, false);
    assert.deepEqual(remainder.availableThrough, user.availableThrough);
    assert.deepEqual(remainder.next, remainder.availableThrough);
    const tail = await request(client.getMessageContent(id, remainder.next, signal));
    assert.deepEqual(tail.from, remainder.next);
    assert.deepEqual(tail.next, remainder.next);
    assert.equal(tail.text, "");
    assert.equal(tail.hasMore, false);
    const output = await request(
      client.getMessageContent(
        id,
        {
          message: assistant.message,
          segmentIndex: 0,
          utf8Offset: 0,
        },
        signal,
      ),
    );
    assert.equal(output.text, "Hello from Urd.");
    assert.equal(output.hasMore, false);
    assert.equal(output.isClosed, true);
    assert.equal(output.finalOutputCommitted, true);
    assert.deepEqual(output.next, assistant.availableThrough);

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
        assert.ok(error instanceof SessionRequestError);
        assert.equal(error.status, 409);
        assert.ok("responseBody" in error);
        return true;
      },
    );
  },
);
