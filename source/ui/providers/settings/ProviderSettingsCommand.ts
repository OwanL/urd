import { RequestError } from "../../http/RequestError.ts";
import type { ProviderDisconnectResult } from "../ProviderCredentials.ts";
import type { ProviderSettingsNotice, ProviderSettingsOperation } from "./ProviderSettingsTypes.ts";

// Commands apply their acknowledgement through the controller and are never cancelled.
export type RunProviderCommand = <T>(
  operation: ProviderSettingsOperation,
  run: () => Promise<T>,
  apply: (value: T) => ProviderDisconnectResult | void,
  options?: {
    modelConnectionId?: string;
    afterSuccess?: (value: T) => boolean;
  },
) => Promise<void>;

export function commandFailureNotice(
  operation: ProviderSettingsOperation,
  error: unknown,
): Exclude<ProviderSettingsNotice, { outcome: "succeeded" }> {
  const httpStatus = error instanceof RequestError ? error.status : undefined;
  const body = error instanceof RequestError ? error.responseBody : undefined;
  if (httpStatus !== undefined && [400, 404, 409, 415, 422].includes(httpStatus)) {
    const copilotNotConfigured =
      operation === "beginSignIn" &&
      body &&
      typeof body === "object" &&
      "error" in body &&
      body.error === "copilotNotConfigured";
    return {
      operation,
      outcome: "rejected",
      httpStatus,
      ...(copilotNotConfigured ? { errorCode: "copilotNotConfigured" } : {}),
    };
  }
  return { operation, outcome: "uncertain", ...(httpStatus !== undefined ? { httpStatus } : {}) };
}
