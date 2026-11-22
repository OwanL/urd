import assert from "node:assert/strict";
import test from "node:test";
import { missingSetupStep, nextSetupStep } from "../../setup/SetupSteps.ts";

void test("choose a directory first, then connect and assign models before reviewing sources", () => {
  const status = {
    required: true,
    workingDirectoryConfigured: false,
    providerConfigured: false,
    bucketsConfigured: false,
  };
  assert.equal(missingSetupStep(status), "directory");
  status.workingDirectoryConfigured = true;
  assert.equal(nextSetupStep("directory", status), "provider");
  status.providerConfigured = true;
  assert.equal(missingSetupStep(status), "models");
  status.bucketsConfigured = true;
  assert.equal(nextSetupStep("models", status), "instructions");
  assert.equal(nextSetupStep("instructions", status), "skills");
  assert.equal(nextSetupStep("skills", status), null);
});

void test("a replacement directory doesn't require repeating configured steps", () => {
  const status = {
    required: true,
    workingDirectoryConfigured: false,
    providerConfigured: true,
    bucketsConfigured: true,
  };
  assert.equal(nextSetupStep("skills", status), "directory");
  status.workingDirectoryConfigured = true;
  assert.equal(nextSetupStep("directory", status), "instructions");
});
