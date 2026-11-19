import assert from "node:assert/strict";
import test from "node:test";
import {
  addBucketModel,
  describeBucket,
  missingModelBuckets,
  removeBucketModel,
} from "../preferences/ModelBucketSelection.ts";

const first = { connectionId: "provider-a", modelId: "vendor/shared" };
const second = { connectionId: "provider-b", modelId: "vendor/shared" };
const another = { connectionId: "provider-a", modelId: "vendor/another" };

void test("adding a bucket member deduplicates pairs, not bare model IDs", () => {
  assert.deepEqual(addBucketModel([first, another], { ...first }), [first, another]);
  assert.deepEqual(addBucketModel([first, another], second), [first, another, second]);
});

void test("removing a bucket member leaves other pairs and returns an empty bucket", () => {
  assert.deepEqual(removeBucketModel([first, second, another], { ...first }), [second, another]);
  assert.deepEqual(removeBucketModel([first], { ...first }), []);
});

void test("only unassigned buckets are listed in the empty chat notice", () => {
  assert.equal(describeBucket([]), "Empty");
  assert.deepEqual(missingModelBuckets({ small: [first], medium: [], large: [] }), [
    "medium",
    "large",
  ]);
  assert.deepEqual(missingModelBuckets({ small: [first], medium: [second], large: [another] }), []);
});
