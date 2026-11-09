import assert from "node:assert/strict";
import test from "node:test";
import { notSentResendMessage, resolveNotSentResendOutcome } from "../app/NotSentResendOutcome.ts";
import type { UserMessageReceipt } from "../sessions/UserMessageSubmission.ts";

const sessionId = "session";
const submissionId = "new-submission";
const accepted: UserMessageReceipt = {
  sessionId,
  submission: {
    submissionId,
    expectedHistoryRevision: 0,
    text: "original input",
    llmSelection: null,
  },
  acceptance: "accepted",
  refusalReason: null,
  executionId: "execution",
};

void test("resend outcomes require the addressed session and the newly issued submission", () => {
  assert.deepEqual(resolveNotSentResendOutcome(accepted, sessionId, submissionId), {
    status: "accepted",
  });
  for (const [receipt, session, submission] of [
    [null, sessionId, submissionId],
    [accepted, sessionId, null],
    [accepted, "other-session", submissionId],
    [accepted, sessionId, "other-submission"],
  ] as const)
    assert.deepEqual(resolveNotSentResendOutcome(receipt, session, submission), {
      status: "uncertain",
    });
  assert.deepEqual(
    resolveNotSentResendOutcome(
      { ...accepted, acceptance: "refused", refusalReason: "capacity", executionId: null },
      sessionId,
      submissionId,
    ),
    { status: "refused", reason: "capacity" },
  );
});

void test("resend feedback describes the explicit outcome without determining it", () => {
  assert.equal(notSentResendMessage({ status: "accepted" }), "Resend accepted.");
  assert.equal(
    notSentResendMessage({ status: "refused", reason: "capacity" }),
    "Not resent: capacity.",
  );
  assert.equal(notSentResendMessage({ status: "uncertain" }), "Resend uncertain.");
  const uncertain = { status: "uncertain", error: "Resend accepted." } as const;
  assert.equal(notSentResendMessage(uncertain), "Resend uncertain: Resend accepted.");
});
