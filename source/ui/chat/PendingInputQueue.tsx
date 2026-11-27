import type { SessionClient } from "../sessions/SessionClient.ts";
import type { PendingInputPreview, SessionSnapshot } from "../sessions/SessionSnapshot.ts";
import type { ChatCommands } from "./ChatCommands.ts";
import { PendingInputBubble } from "./PendingInputBubble.tsx";

export function PendingInputQueue({
  placement,
  client,
  sessionId,
  snapshot,
  commands,
  ready,
}: {
  placement: "preparing" | "queued";
  client: SessionClient;
  sessionId: string;
  snapshot: SessionSnapshot | null;
  commands: ChatCommands;
  ready: boolean;
}) {
  const pendingInputs = snapshot?.pendingInputs ?? [];
  const pendingExecutionId =
    snapshot?.queuedInputExecutionId ?? snapshot?.unsettledExecutionId ?? null;
  const inputs = pendingInputs.filter((input) => input.isClaimed === (placement === "preparing"));
  const unlistedRemovals =
    placement === "queued"
      ? Object.values(commands.status.pendingInputRemovals).filter(
          (removal) =>
            removal.sessionId === sessionId &&
            (pendingExecutionId !== removal.executionId ||
              !pendingInputs.some((input) => input.submissionId === removal.submissionId)),
        )
      : [];

  function renderPendingInput(input: PendingInputPreview) {
    const executionId = pendingExecutionId;
    const removal = executionId
      ? commands.pendingInputRemoval(sessionId, executionId, input.submissionId)
      : undefined;
    const onRemove =
      executionId && input.canRemove
        ? () => {
            void commands.removePendingInput(sessionId, executionId, input.submissionId, true);
          }
        : undefined;
    const onCheck =
      executionId && removal?.status === "uncertain"
        ? () => {
            void commands.checkPendingInputRemoval(sessionId, executionId, input.submissionId);
          }
        : undefined;
    return (
      <PendingInputBubble
        key={input.submissionId}
        client={client}
        sessionId={sessionId}
        submissionId={input.submissionId}
        preview={input.preview}
        removalStatus={removal?.status}
        removeLabel={onRemove ? `Remove queued input: ${input.preview}` : undefined}
        checkLabel={`Check removal status for pending input: ${input.preview}`}
        disabled={!ready}
        onRemove={onRemove}
        onCheck={onCheck}
      />
    );
  }

  if (inputs.length === 0 && unlistedRemovals.length === 0) return null;
  return (
    <section
      class="chat-pending-inputs"
      aria-label={placement === "preparing" ? "Preparing inputs" : "Queued inputs"}
    >
      <div class="chat-pending-inputs-label">
        {placement === "preparing"
          ? "Preparing…"
          : inputs.length
            ? `Queued · ${inputs.length}`
            : "Queue update"}
      </div>
      {inputs.map(renderPendingInput)}
      {unlistedRemovals.map((removal) => (
        <PendingInputBubble
          key={removal.submissionId}
          preview={null}
          removalStatus={removal.status}
          checkLabel={`Check removal status for input ${removal.submissionId}`}
          onCheck={
            removal.status === "uncertain"
              ? () => {
                  void commands.checkPendingInputRemoval(
                    sessionId,
                    removal.executionId,
                    removal.submissionId,
                  );
                }
              : undefined
          }
        />
      ))}
    </section>
  );
}
