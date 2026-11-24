import type {
  AuxiliaryModelPreferences,
  ConfiguredModel,
  ModelBucket,
  ReasoningEffort,
} from "./PreferencesClient.ts";

export function modelKey(model: ConfiguredModel): string {
  return `${model.connectionId}/${model.modelId}`;
}

export function addBucketModel(
  models: readonly ConfiguredModel[],
  model: ConfiguredModel,
): readonly ConfiguredModel[] {
  const key = modelKey(model);
  return models.some((member) => modelKey(member) === key) ? models : [...models, model];
}

export function removeBucketModel(
  models: readonly ConfiguredModel[],
  model: ConfiguredModel,
): readonly ConfiguredModel[] {
  const key = modelKey(model);
  return models.filter((member) => modelKey(member) !== key);
}

// Replaces the effort of the entry matching the pair; an unchanged list returns the input.
export function setBucketModelEffort(
  models: readonly ConfiguredModel[],
  model: ConfiguredModel,
  effort: ReasoningEffort,
): readonly ConfiguredModel[] {
  const key = modelKey(model);
  let changed = false;
  const next = models.map((member) => {
    if (modelKey(member) !== key || member.reasoningEffort === effort) return member;
    changed = true;
    return { ...member, reasoningEffort: effort };
  });
  return changed ? next : models;
}

export function describeBucket(models: readonly ConfiguredModel[]): string {
  return models.length ? `${models.length} ${models.length === 1 ? "model" : "models"}` : "Empty";
}

export function missingModelBuckets(
  choices: Pick<AuxiliaryModelPreferences, ModelBucket>,
): ModelBucket[] {
  const buckets: ModelBucket[] = ["small", "medium", "large"];
  return buckets.filter((bucket) => choices[bucket].length === 0);
}
