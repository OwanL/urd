export const setupSteps = ["directory", "provider", "models", "instructions", "skills"] as const;
export type SetupStep = (typeof setupSteps)[number];

export interface SetupStatus {
  required: boolean;
  providerConfigured: boolean;
  bucketsConfigured: boolean;
  workingDirectoryConfigured: boolean;
}

export function missingSetupStep(status: SetupStatus) {
  if (!status.workingDirectoryConfigured) return "directory";
  if (!status.providerConfigured) return "provider";
  if (!status.bucketsConfigured) return "models";
  return null;
}

export function nextSetupStep(step: SetupStep, status: SetupStatus) {
  const missing = missingSetupStep(status);
  if (missing) return missing;
  if (step === "skills") return null;
  return step === "instructions" ? "skills" : "instructions";
}
