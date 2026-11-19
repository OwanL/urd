import assert from "node:assert/strict";
import test from "node:test";
import { agentAskTexts, agentAnswerText } from "../chat/AgentCallText.ts";
import type { ToolCallTranscript } from "../sessions/Messages.ts";

function transcript(input: string, output: string | null): ToolCallTranscript {
  return {
    input,
    output,
    truncated: false,
    projectionFinal: true,
    inputTokens: 1,
    outputTokens: 1,
  };
}

void test("ask calls read the prompt argument and the output answer", () => {
  assert.deepEqual(
    agentAskTexts(transcript(JSON.stringify({ prompt: "Keep the token path?" }), "Remove it.")),
    { prompt: "Keep the token path?", answer: "Remove it." },
  );
  assert.deepEqual(
    agentAskTexts(transcript(JSON.stringify({ prompt: "Keep the token path?" }), null)),
    {
      prompt: "Keep the token path?",
      answer: null,
    },
  );
  assert.deepEqual(agentAskTexts(transcript("{not json", "Remove it.")), {
    prompt: null,
    answer: "Remove it.",
  });
});

void test("answer calls read the answer argument instead of the output", () => {
  assert.equal(
    agentAnswerText(transcript(JSON.stringify({ answer: "Remove it." }), "ok")),
    "Remove it.",
  );
  assert.equal(agentAnswerText(transcript(JSON.stringify({}), "ok")), null);
  assert.equal(agentAnswerText(transcript("{not json", "ok")), null);
});
