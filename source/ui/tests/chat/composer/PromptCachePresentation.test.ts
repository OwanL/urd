import assert from "node:assert/strict";
import test from "node:test";
import { promptCacheText } from "../../../chat/composer/PromptCachePresentation.ts";

void test("cache percentages use the token totals and keep partial coverage explicit", () => {
  const totals = {
    inputTokens: 3_189_178,
    cachedInputTokens: 3_057_152,
    reportedRequests: 45,
    totalRequests: 46,
  };
  assert.equal(promptCacheText(totals), "95.9% cached · 132,026 uncached (partial)");
  assert.equal(
    promptCacheText({ ...totals, totalRequests: 45 }),
    "95.9% cached · 132,026 uncached",
  );
});

void test("reported zero is distinct from missing cache data and no activity", () => {
  const missing = { inputTokens: 0, cachedInputTokens: 0, reportedRequests: 0, totalRequests: 2 };
  const cold = { ...missing, inputTokens: 23_686, reportedRequests: 2 };
  assert.equal(promptCacheText(cold), "0% cached · 23,686 uncached");
  assert.equal(promptCacheText(missing), "Not reported");
  assert.equal(promptCacheText({ ...missing, totalRequests: 0 }), "No recorded requests");
  assert.equal(promptCacheText(null), "No completed turn");
});

void test("rounded percentages do not turn a small miss or hit into an exact one", () => {
  const totals = {
    inputTokens: 100_000,
    cachedInputTokens: 1,
    reportedRequests: 1,
    totalRequests: 1,
  };
  assert.equal(promptCacheText(totals), "<0.1% cached · 99,999 uncached");
  assert.equal(
    promptCacheText({ ...totals, cachedInputTokens: 99_999 }),
    ">99.9% cached · 1 uncached",
  );
  assert.equal(
    promptCacheText({ ...totals, cachedInputTokens: 100_000 }),
    "100% cached · 0 uncached",
  );
});
