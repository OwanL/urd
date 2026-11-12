import type { ToolCallTranscript } from "../sessions/Messages.ts";

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
