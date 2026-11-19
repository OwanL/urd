import { useId, useRef } from "preact/hooks";
import { Icon } from "../controls/Icon.tsx";
import { missingModelBuckets } from "../preferences/ModelBucketSelection.ts";
import type { ModelBucket } from "../preferences/PreferencesClient.ts";
import type { PreferencesState } from "../preferences/usePreferences.ts";
import { ModelPicker } from "../providers/models/ModelPicker.tsx";
import type { ModelOptionsState } from "../providers/models/useModelOptions.ts";

const bucketLabels: Record<ModelBucket, string> = {
  small: "Small",
  medium: "Medium",
  large: "Large",
};

export function ModelBucketSetup({
  preferences,
  models,
  disabled,
  onCompleteFocus,
}: {
  preferences: PreferencesState;
  models: ModelOptionsState;
  disabled: boolean;
  onCompleteFocus(): void;
}) {
  const headingId = useId();
  const section = useRef<HTMLElement>(null);
  if (!preferences.preferences) return null;
  const missing = missingModelBuckets(preferences.preferences.modelChoices);
  if (!missing.length) return null;

  async function assign(bucket: ModelBucket, key: string) {
    const option = models.options.find((model) => model.key === key);
    if (!option) return;
    const { connection, modelId } = option.selection;
    const hadFocus = section.current?.contains(document.activeElement);
    const saved = await preferences.saveBucketModels(bucket, [
      { connectionId: connection.id, modelId },
    ]);
    if (!saved || !hadFocus) return;
    requestAnimationFrame(() => {
      if (document.activeElement !== document.body) return;
      const next = section.current?.querySelector<HTMLButtonElement>(
        ".model-picker-trigger:not(:disabled)",
      );
      if (next?.getClientRects().length) next.focus({ preventScroll: true });
      else onCompleteFocus();
    });
  }

  const error = missing
    .map((bucket) => preferences.errorFor(`modelBucket:${bucket}`))
    .find(Boolean);
  return (
    <section ref={section} class="model-bucket-setup" aria-labelledby={headingId}>
      <h2 class="model-bucket-setup-heading" id={headingId}>
        <Icon name="warning" />
        <span>Unassigned model buckets</span>
      </h2>
      <dl class="model-bucket-setup-rows">
        {missing.map((bucket) => {
          const name = bucketLabels[bucket];
          return (
            <div class="model-bucket-setup-row" key={bucket}>
              <dt>{name}</dt>
              <dd>
                <ModelPicker
                  models={models.options}
                  modelKey={null}
                  modelLabel="Assign model"
                  ariaLabel={`Assign model to ${name} bucket`}
                  onModelChange={(key) => void assign(bucket, key)}
                  loadingModels={models.loading}
                  refreshingModels={models.refreshing}
                  modelsError={models.error}
                  onRetryModels={() => void models.retry()}
                  disabled={disabled || preferences.loading || preferences.pending}
                />
                {preferences.pendingFor === `modelBucket:${bucket}` && (
                  <span role="status">Saving…</span>
                )}
              </dd>
            </div>
          );
        })}
      </dl>
      {error && (
        <p class="model-bucket-setup-error" role="alert">
          {error}
        </p>
      )}
    </section>
  );
}
