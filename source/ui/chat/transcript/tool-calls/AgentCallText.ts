import type { ToolCallTranscript } from "../../../sessions/transcript/Messages.ts";

export interface AgentCallTexts {
  readonly prompt: string | null;
  readonly answer: string | null;
}

function jsonArguments(input: string): { readonly [key: string]: unknown } | null {
  try {
    const parsed: unknown = JSON.parse(input);
    if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) return null;
    return parsed as { readonly [key: string]: unknown };
  } catch {
    return null;
  }
}

function argumentString(
  arguments_: { readonly [key: string]: unknown },
  name: string,
): string | null {
  const value = arguments_[name];
  return typeof value === "string" && value.length > 0 ? value : null;
}

// ask_agent input is { prompt }; its answer arrives as the call output string.
export function agentAskTexts(transcript: ToolCallTranscript): AgentCallTexts {
  const arguments_ = jsonArguments(transcript.input);
  return {
    prompt: arguments_ ? argumentString(arguments_, "prompt") : null,
    answer: transcript.output,
  };
}

// answer_agent input is { answer }; its output only acknowledges the reply.
export function agentAnswerText(transcript: ToolCallTranscript): string | null {
  const arguments_ = jsonArguments(transcript.input);
  return arguments_ ? argumentString(arguments_, "answer") : null;
}
