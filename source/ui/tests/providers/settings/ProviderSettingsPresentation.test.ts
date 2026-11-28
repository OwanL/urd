import assert from "node:assert/strict";
import test from "node:test";
import type { ProviderConnection } from "../../../providers/ProviderConnection.ts";
import type { ProviderCredentialStatus } from "../../../providers/ProviderCredentials.ts";
import type { ProviderSignIn } from "../../../providers/ProviderSignIn.ts";
import { RequestError } from "../../../http/RequestError.ts";
import { commandFailureNotice } from "../../../providers/settings/ProviderSettingsCommand.ts";
import {
  connectionAttention,
  copilotSignInError,
  noticeText,
} from "../../../providers/settings/ProviderSettingsPresentation.ts";

const connection: ProviderConnection = {
  id: "provider",
  name: "Provider",
  route: "chatGpt",
  api: "openAIResponses",
  endpoint: "https://api.example.com",
  authentication: "oAuth",
};
const credentials: ProviderCredentialStatus = {
  connectionId: connection.id,
  hasCredentials: true,
  requiresSignIn: false,
  isDisconnecting: false,
  accountLabel: null,
  expiresAt: null,
  planAccessGranted: null,
};
const state = { credentials: { provider: credentials }, signIns: {}, errors: {} };

void test("provider attention distinguishes healthy credentials, unknown status and no authentication", () => {
  assert.equal(connectionAttention(state, connection), null);
  assert.equal(connectionAttention({ ...state, credentials: {} }, connection), "Checking…");
  assert.equal(
    connectionAttention({ ...state, credentials: {} }, { ...connection, authentication: "none" }),
    null,
  );
  assert.equal(
    connectionAttention(
      { ...state, credentials: { provider: { ...credentials, requiresSignIn: true } } },
      connection,
    ),
    "Sign-in required",
  );
});

void test("missing Copilot OAuth configuration rejects sign-in with installation guidance", () => {
  const notice = commandFailureNotice(
    "beginSignIn",
    new RequestError(409, { error: "copilotNotConfigured" }),
  );
  assert.equal(notice.outcome, "rejected");
  assert.match(noticeText(notice)!, /URD_GITHUB_COPILOT_CLIENT_ID.*restart Urd/);
});

void test("Copilot authorization failures show actionable copy rather than provider codes", () => {
  assert.match(copilotSignInError("PlanAccessDenied")!, /denied Copilot access.*OAuth app/);
  assert.match(copilotSignInError("SignInRejected")!, /didn't authorize/);
  assert.equal(copilotSignInError("UnknownProviderCode"), null);
});

void test("pending sign-in and failed credential reads remain visible over healthy credentials", () => {
  const signIn: ProviderSignIn = {
    id: "sign-in",
    connectionId: connection.id,
    status: "pending",
    authorizationUrl: null,
    expiresAt: "2030-01-01T00:00:00Z",
    error: null,
  };
  const failed = { ...state, errors: { "credentials:provider": "read failed" } };
  assert.equal(connectionAttention(failed, connection), "Status unavailable");
  assert.equal(
    connectionAttention({ ...failed, signIns: { provider: signIn } }, connection),
    "Signing in…",
  );
  assert.equal(
    connectionAttention(
      { ...state, signIns: { provider: { ...signIn, status: "completing" } } },
      connection,
    ),
    "Completing sign-in…",
  );
  assert.equal(
    connectionAttention(
      { ...state, signIns: { provider: { ...signIn, status: "completed" } } },
      connection,
    ),
    null,
  );
});
