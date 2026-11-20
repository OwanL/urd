import assert from "node:assert/strict";
import test from "node:test";
import { SessionClient } from "../../sessions/SessionClient.ts";
import { testSessionMappings, type MappingCase } from "./SessionClientMapping.ts";
import {
  sessionId,
  executionId,
  submissionId,
  boundaryId,
  cursor,
  content,
  promptRef,
  positionSummary,
  positionWindow,
  promptPage,
  prepassDetails,
} from "./SessionClientFixtures.ts";

const mappings: MappingCase[] = [
  {
    name: "latest history is fenced by revision",
    call: (client) => client.getHistory(sessionId, { mode: "latest", expectedHistoryRevision: 7 }),
    method: "GET",
    path: `/api/sessions/${sessionId}/history`,
    query: { mode: "latest", expectedHistoryRevision: "7" },
    response: {
      sessionId,
      historyRevision: 7,
      entries: [],
      hasBefore: true,
      hasAfter: false,
      settlements: [],
    },
  },
  {
    name: "anchored history preserves its exact entry reference",
    call: (client) =>
      client.getHistory(sessionId, {
        mode: "around",
        expectedHistoryRevision: 7,
        anchor: promptRef,
      }),
    method: "GET",
    path: `/api/sessions/${sessionId}/history`,
    query: {
      mode: "around",
      expectedHistoryRevision: "7",
      executionId,
      entryId: submissionId,
      kind: "user",
    },
    response: {
      sessionId,
      historyRevision: 7,
      entries: [],
      hasBefore: false,
      hasAfter: false,
      settlements: [],
    },
  },
  {
    name: "position summary is fenced by history revision",
    call: (client) => client.getPositionSummary(sessionId, 7),
    method: "GET",
    path: `/api/sessions/${sessionId}/positions`,
    query: { expectedHistoryRevision: "7" },
    response: positionSummary,
  },
  {
    name: "weighted locate sends a normalized fraction",
    call: (client) => client.locatePosition(sessionId, 7, 0.625),
    method: "GET",
    path: `/api/sessions/${sessionId}/positions/locate`,
    query: { expectedHistoryRevision: "7", fraction: "0.625" },
    response: positionWindow,
  },
  {
    name: "prompt pages send a bounded keyset cursor",
    call: (client) =>
      client.getPromptMarkers(sessionId, { expectedHistoryRevision: 7, afterRank: 2, limit: 20 }),
    method: "GET",
    path: `/api/sessions/${sessionId}/positions/prompts`,
    query: { expectedHistoryRevision: "7", afterRank: "2", limit: "20" },
    response: promptPage,
  },
  {
    name: "prompt activation uses exact stable user identity",
    call: (client) => client.activatePrompt(sessionId, promptRef),
    method: "GET",
    path: `/api/sessions/${sessionId}/positions/prompts/${executionId}/${submissionId}`,
    query: { expectedHistoryRevision: "7" },
    response: positionWindow,
  },
  {
    name: "content flattens every cursor member",
    call: (client) => client.getMessageContent(sessionId, cursor),
    method: "GET",
    path: `/api/sessions/${sessionId}/content`,
    query: {
      executionId,
      messageId: cursor.message.messageId,
      role: "assistant",
      historyRevision: "7",
      segmentIndex: "3",
      utf8Offset: "11",
    },
    response: content,
  },
  {
    name: "prepass details page by revision and offsets",
    call: (client) =>
      client.getPrepassDetails(sessionId, {
        boundaryId,
        category: "skills",
        historyRevision: 7,
        candidateOffset: 3,
        resultOffset: 1,
      }),
    method: "GET",
    path: `/api/sessions/${sessionId}/prepass/${boundaryId}/skills`,
    query: { historyRevision: "7", candidateOffset: "3", resultOffset: "1" },
    response: prepassDetails,
  },
];

testSessionMappings(mappings);

void test("position client rejects an invalid fraction without sending a request", (t) => {
  const fetchMock = t.mock.method(globalThis, "fetch", async () => new Response("null"));
  const client = new SessionClient(new URL("https://server.example"));

  assert.throws(() => client.locatePosition(sessionId, 7, 1.1), TypeError);
  assert.equal(fetchMock.mock.callCount(), 0);
});
