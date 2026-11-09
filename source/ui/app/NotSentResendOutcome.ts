import type {
  MessageRefusalReason,
  UserMessageReceipt,
} from "../sessions/UserMessageSubmission.ts";

export type NotSentResendOutcome =
  | { status: "accepted" }
  | { status: "refused"; reason: MessageRefusalReason | null }
  | { status: "uncertain"; error?: string };

export function resolveNotSentResendOutcome(
  receipt: UserMessageReceipt | null,
  sessionId: string,
  submissionId: string | null,
): NotSentResendOutcome {
  if (
    receipt === null ||
    submissionId === null ||
    receipt.sessionId !== sessionId ||
    receipt.submission.submissionId !== submissionId
  )
    return { status: "uncertain" };
  return receipt.acceptance === "accepted"
    ? { status: "accepted" }
    : { status: "refused", reason: receipt.refusalReason };
}

export function notSentResendMessage(outcome: NotSentResendOutcome): string {
  if (outcome.status === "accepted") return "Resend accepted.";
  if (outcome.status === "refused") return `Not resent: ${outcome.reason}.`;
  return outcome.error === undefined ? "Resend uncertain." : `Resend uncertain: ${outcome.error}`;
}
