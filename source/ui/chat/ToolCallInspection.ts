import type { ToolCallTranscript } from "../sessions/Messages.ts";

interface ToolCallPreviewIdentity {
  readonly id: number;
  readonly requestId: string;
  readonly callId: string;
  readonly historyRevision: number;
  readonly payloadFinished: boolean;
}

interface ReadyToolCallPreview extends ToolCallPreviewIdentity {
  readonly transcript: ToolCallTranscript;
}

export function reusableToolCallPreview(
  preview: ReadyToolCallPreview | null,
  target: ToolCallPreviewIdentity,
  currentHistoryRevision: number | null,
): ToolCallTranscript | null {
  if (
    !preview ||
    !target.payloadFinished ||
    !preview.payloadFinished ||
    currentHistoryRevision !== target.historyRevision ||
    preview.id !== target.id ||
    preview.requestId !== target.requestId ||
    preview.callId !== target.callId ||
    preview.historyRevision !== target.historyRevision ||
    preview.transcript.truncated ||
    !preview.transcript.projectionFinal ||
    preview.transcript.output === null
  )
    return null;
  return preview.transcript;
}

export function formatToolInput(input: string): string {
  try {
    return JSON.stringify(JSON.parse(input), null, 2);
  } catch {
    return input;
  }
}

export function toolCallClipboardText(transcript: ToolCallTranscript): string {
  return `Input\n${formatToolInput(transcript.input)}\n\nOutput\n${transcript.output ?? "Running…"}`;
}
