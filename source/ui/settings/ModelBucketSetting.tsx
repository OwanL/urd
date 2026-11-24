import { useMemo, useRef } from "preact/hooks";
import type { ModelPickerOption } from "../providers/models/ModelPicker.tsx";
import { ModelPicker } from "../providers/models/ModelPicker.tsx";
import { IconButton } from "../controls/IconButton.tsx";
import { ReasoningPicker, reasoningEffortOptions } from "../chat/composer/ReasoningPicker.tsx";
import {
  addBucketModel,
  describeBucket,
  modelKey,
  removeBucketModel,
  setBucketModelEffort,
} from "../preferences/ModelBucketSelection.ts";
import type { ConfiguredModel, ReasoningEffort } from "../preferences/PreferencesClient.ts";

export function ModelBucketSetting({
  name,
  models,
  members,
  label,
  modelForKey,
  busy,
  catalogReady,
  pending,
  error,
  save,
}: {
  name: string;
  models: readonly ModelPickerOption[];
  members: readonly ConfiguredModel[];
  label(model: ConfiguredModel): string;
  modelForKey(key: string): ConfiguredModel | null;
  busy: boolean;
  catalogReady: boolean;
  pending: boolean;
  error: string | null;
  save(models: readonly ConfiguredModel[]): Promise<boolean>;
}) {
  const heading = useRef<HTMLHeadingElement>(null);
  const available = useMemo(() => {
    const keys = new Set(members.map(modelKey));
    return models.filter(({ key }) => !keys.has(key));
  }, [members, models]);

  async function remove(model: ConfiguredModel, control: HTMLButtonElement) {
    if (busy) return;
    const hadFocus = document.activeElement === control;
    const saved = await save(removeBucketModel(members, model));
    if (saved && hadFocus)
      requestAnimationFrame(() => {
        if (document.activeElement === document.body && heading.current?.getClientRects().length)
          heading.current.focus({ preventScroll: true });
      });
  }

  return (
    <section class="model-bucket" aria-label={`${name} bucket`}>
      <div class="model-bucket-heading">
        <h4 ref={heading} tabIndex={-1}>
          {name}
        </h4>
        <span class="model-bucket-summary">{describeBucket(members)}</span>
        <ModelPicker
          models={available}
          modelKey={null}
          modelLabel="Add model"
          ariaLabel={`Add model to ${name} bucket`}
          onModelChange={(key) => {
            const model = modelForKey(key);
            if (!model) return;
            const next = addBucketModel(members, model);
            if (next !== members) void save(next);
          }}
          disabled={busy || !catalogReady}
        />
      </div>
      {members.length > 0 && (
        <ul class="model-bucket-members">
          {members.map((model) => {
            const modelLabel = label(model);
            return (
              <li key={modelKey(model)}>
                <span>{modelLabel}</span>
                <ReasoningPicker
                  effort={model.reasoningEffort ?? null}
                  options={reasoningEffortOptions}
                  disabled={busy}
                  onChange={(effort) =>
                    void save(setBucketModelEffort(members, model, effort as ReasoningEffort))
                  }
                />
                <IconButton
                  icon="close"
                  label={`Remove ${modelLabel} from ${name} bucket`}
                  hint="Remove model"
                  aria-disabled={busy}
                  onClick={(event) => void remove(model, event.currentTarget)}
                />
              </li>
            );
          })}
        </ul>
      )}
      {pending && (
        <span class="model-bucket-feedback" role="status">
          Saving…
        </span>
      )}
      {error && (
        <span class="model-bucket-feedback" role="alert">
          {error}
        </span>
      )}
    </section>
  );
}
