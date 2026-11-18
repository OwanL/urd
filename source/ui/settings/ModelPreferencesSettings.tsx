import { useCallback, useEffect, useMemo, useRef, useState } from "preact/hooks";
import { WorkspaceModels } from "../app/WorkspaceModels.ts";
import { ModelPicker } from "../chat/ModelPicker.tsx";
import type { ComposerModel } from "../chat/ComposerTypes.ts";
import type { ModelOption } from "../app/WorkspaceModels.ts";
import type { PreferencesState } from "../preferences/usePreferences.ts";
import { describeBucket, modelKey } from "../preferences/ModelBucketSelection.ts";
import { ModelBucketSetting } from "./ModelBucketSetting.tsx";
import type {
  AuxiliaryModelPurpose,
  ConfiguredModel,
  ModelBucket,
  ModelChoice,
} from "../preferences/PreferencesClient.ts";
import type { ProviderClient } from "../providers/ProviderClient.ts";

const buckets: readonly ModelBucket[] = ["small", "medium", "large"];
const emptyBucket: readonly ConfiguredModel[] = [];
const purposes: readonly AuxiliaryModelPurpose[] = ["tools", "skills", "instructions", "title"];
const bucketNames: Record<ModelBucket, string> = {
  small: "Small",
  medium: "Medium",
  large: "Large",
};
const purposeNames: Record<AuxiliaryModelPurpose, string> = {
  tools: "Tools",
  skills: "Skills",
  instructions: "Instructions",
  title: "Title",
};

interface CachedModels {
  readonly options: readonly ModelOption[];
  readonly providers: readonly { readonly id: string; readonly name: string }[];
  readonly loading: boolean;
  readonly loaded: boolean;
  readonly error: string | null;
}

export function ModelPreferencesSettings({
  client,
  state,
  view,
  active,
}: {
  client: ProviderClient;
  state: PreferencesState;
  view: "buckets" | "purposes";
  active: boolean;
}) {
  const reader = useMemo(() => new WorkspaceModels(client), [client]);
  const [catalog, setCatalog] = useState<CachedModels>({
    options: [],
    providers: [],
    loading: false,
    loaded: false,
    error: null,
  });
  const readAbort = useRef<AbortController | null>(null);
  const readCachedModels = useCallback(() => {
    readAbort.current?.abort();
    const abort = new AbortController();
    readAbort.current = abort;
    setCatalog((current) => ({ ...current, loading: true, error: null }));
    void reader
      .readCached(abort.signal)
      .then((result) => {
        if (readAbort.current !== abort || abort.signal.aborted) return;
        setCatalog({
          options: result.options ?? [],
          providers: (result.providers ?? []).map(({ id, name }) => ({ id, name })),
          loading: false,
          loaded: result.options !== null,
          error: result.error ? "Saved model catalogs could not all be read." : null,
        });
      })
      .catch(() => {
        if (readAbort.current !== abort || abort.signal.aborted) return;
        setCatalog((current) => ({
          ...current,
          loading: false,
          loaded: true,
          error: "Saved model catalogs could not be read.",
        }));
      });
  }, [reader]);

  useEffect(() => {
    if (!active) return;
    readCachedModels();
    return () => {
      readAbort.current?.abort();
      readAbort.current = null;
    };
  }, [active, readCachedModels]);

  const models: readonly ComposerModel[] = useMemo(
    () =>
      catalog.options.map(({ key, modelLabel, providerLabel }) => ({
        key,
        modelLabel,
        providerLabel,
      })),
    [catalog.options],
  );
  const modelByKey = useMemo(
    () => new Map(catalog.options.map((option) => [option.key, option] as const)),
    [catalog.options],
  );
  const configuredModelLabel = (model: ConfiguredModel | null): string => {
    if (!model) return "Not configured";
    const option = modelByKey.get(modelKey(model));
    if (option) return `${option.providerLabel} · ${option.modelLabel}`;
    const provider = catalog.providers.find(({ id }) => id === model.connectionId);
    const identity = `${provider?.name ?? model.connectionId} · ${model.modelId}`;
    if (!catalog.loaded && !catalog.error)
      return `${catalog.loading ? "Checking saved catalog" : "Saved model"} · ${identity}`;
    if (catalog.error) return `Catalog unavailable · ${identity}`;
    return `Unavailable · ${identity}`;
  };
  const configuredModel = (
    key: string,
    unavailable: ConfiguredModel | null = null,
  ): ConfiguredModel | null => {
    const option = modelByKey.get(key);
    if (option)
      return { connectionId: option.selection.connection.id, modelId: option.selection.modelId };
    return unavailable && modelKey(unavailable) === key ? unavailable : null;
  };
  const pickerModels = (saved: ConfiguredModel | null): readonly ComposerModel[] => {
    if (!saved || modelByKey.has(modelKey(saved))) return models;
    const provider = catalog.providers.find(({ id }) => id === saved.connectionId);
    return [
      ...models,
      {
        key: modelKey(saved),
        modelLabel: `${saved.modelId} (unavailable)`,
        providerLabel: provider?.name ?? saved.connectionId,
      },
    ];
  };
  const busy = !state.loaded || state.pending;

  return (
    <section
      class="preference-model-settings"
      aria-label={view === "buckets" ? "Model buckets" : "Purpose choices"}
    >
      {view === "purposes" && <h3>Purpose choices</h3>}
      {catalog.error && (
        <div class="general-settings-feedback" aria-live="polite" aria-atomic="true">
          <p role="alert">{catalog.error}</p>
          <button
            type="button"
            aria-label="Retry loading model catalogs"
            disabled={catalog.loading}
            onClick={readCachedModels}
          >
            Retry
          </button>
        </div>
      )}
      {view === "buckets" && (
        <>
          <p class="model-bucket-explanation">
            Urd picks an eligible model at random. For subagents, Auto uses the provider’s catalog
            when no bucket entry applies.
          </p>
          {!state.loaded ? (
            <div class="general-settings-feedback">
              {state.error ? (
                <>
                  <p role="alert">{state.error}</p>
                  <button
                    type="button"
                    disabled={state.loading}
                    onClick={() => void state.refresh()}
                  >
                    Retry preferences
                  </button>
                </>
              ) : (
                <p role="status">Loading preferences…</p>
              )}
            </div>
          ) : (
            <div class="preference-model-group">
              {buckets.map((bucket) => {
                const preferenceKey = `modelBucket:${bucket}` as const;
                return (
                  <ModelBucketSetting
                    key={bucket}
                    name={bucketNames[bucket]}
                    models={models}
                    members={state.preferences?.modelChoices[bucket] ?? emptyBucket}
                    label={configuredModelLabel}
                    modelForKey={configuredModel}
                    busy={busy}
                    catalogReady={catalog.loaded && !catalog.loading}
                    pending={state.pendingFor === preferenceKey}
                    error={state.errorFor(preferenceKey)}
                    save={(next) => state.saveBucketModels(bucket, next)}
                  />
                );
              })}
            </div>
          )}
        </>
      )}
      {view === "purposes" && (
        <div class="preference-model-group">
          {purposes.map((purpose) => (
            <AuxiliaryModelChoice
              key={purpose}
              purpose={purpose}
              state={state}
              loadingModels={catalog.loading}
              catalogLoaded={catalog.loaded}
              catalogError={catalog.error}
              readCachedModels={readCachedModels}
              configuredModel={configuredModel}
              configuredModelLabel={configuredModelLabel}
              pickerModels={pickerModels}
              busy={busy}
            />
          ))}
        </div>
      )}
    </section>
  );
}

function AuxiliaryModelChoice({
  purpose,
  state,
  loadingModels,
  catalogLoaded,
  catalogError,
  readCachedModels,
  configuredModel,
  configuredModelLabel,
  pickerModels,
  busy,
}: {
  purpose: AuxiliaryModelPurpose;
  state: PreferencesState;
  loadingModels: boolean;
  catalogLoaded: boolean;
  catalogError: string | null;
  readCachedModels(): void;
  configuredModel(key: string, unavailable?: ConfiguredModel | null): ConfiguredModel | null;
  configuredModelLabel(model: ConfiguredModel | null): string;
  pickerModels(model: ConfiguredModel | null): readonly ComposerModel[];
  busy: boolean;
}) {
  const saved = state.preferences?.modelChoices[purpose] ?? null;
  const savedBucket = saved?.bucket;
  const savedConnectionId = saved?.connectionId;
  const savedModelId = saved?.modelId;
  const [specific, setSpecific] = useState(saved?.bucket === null);
  const [draftChoice, setDraftChoice] = useState<ModelChoice | null>(saved);
  const preferenceKey = `modelChoice:${purpose}` as const;

  useEffect(() => {
    const savedChoice: ModelChoice | null =
      savedBucket === undefined
        ? null
        : {
            bucket: savedBucket,
            connectionId: savedConnectionId ?? null,
            modelId: savedModelId ?? null,
          };
    setSpecific(savedChoice?.bucket === null);
    setDraftChoice(savedChoice);
  }, [savedBucket, savedConnectionId, savedModelId]);

  const savedModel =
    saved && saved.bucket === null && saved.connectionId && saved.modelId
      ? { connectionId: saved.connectionId, modelId: saved.modelId }
      : null;
  const draftModel =
    draftChoice && draftChoice.bucket === null && draftChoice.connectionId && draftChoice.modelId
      ? { connectionId: draftChoice.connectionId, modelId: draftChoice.modelId }
      : savedModel;
  const pickerLabel = `${purposeNames[purpose]} specific model · ${configuredModelLabel(draftModel)}`;
  const bucketModels = saved?.bucket ? state.preferences?.modelChoices[saved.bucket] : undefined;

  const chooseBucket = async (value: string) => {
    state.clearError();
    if (value === "specific") {
      setSpecific(true);
      setDraftChoice(null);
      return;
    }
    if (!buckets.includes(value as ModelBucket)) return;
    const bucket = value as ModelBucket;
    const next: ModelChoice = { bucket, connectionId: null, modelId: null };
    setSpecific(false);
    setDraftChoice(next);
    if (!(await state.saveModelChoice(purpose, next))) {
      setSpecific(saved?.bucket === null);
      setDraftChoice(saved);
    }
  };

  const chooseModel = async (key: string) => {
    const model = configuredModel(key, draftModel);
    if (!model) return;
    const next: ModelChoice = { bucket: null, ...model };
    setDraftChoice(next);
    if (!(await state.saveModelChoice(purpose, next))) setDraftChoice(saved);
  };

  return (
    <div class="preference-model-row preference-purpose-row" key={purpose}>
      <label class="preference-model-name" htmlFor={`auxiliary-model-${purpose}`}>
        {purposeNames[purpose]}
      </label>
      <select
        id={`auxiliary-model-${purpose}`}
        value={specific ? "specific" : (saved?.bucket ?? "specific")}
        disabled={busy}
        onChange={(event) => void chooseBucket(event.currentTarget.value)}
      >
        {buckets.map((bucket) => (
          <option key={bucket} value={bucket}>
            {bucketNames[bucket]}
          </option>
        ))}
        <option value="specific">Specific model</option>
      </select>
      {specific && (
        <ModelPicker
          models={pickerModels(draftModel)}
          modelKey={draftModel ? modelKey(draftModel) : null}
          modelLabel={pickerLabel}
          onModelChange={(key) => void chooseModel(key)}
          loadingModels={loadingModels}
          refreshingModels={loadingModels}
          modelsError={catalogError}
          onRetryModels={readCachedModels}
          disabled={busy || !catalogLoaded || loadingModels}
        />
      )}
      {!specific && saved?.bucket && (
        <span class="preference-model-resolution">
          {bucketModels?.length ? describeBucket(bucketModels) : "No models configured"}
        </span>
      )}
      <SettingFeedback
        pending={state.pendingFor === preferenceKey}
        error={state.errorFor(preferenceKey)}
        loaded={state.loaded}
      />
    </div>
  );
}

function SettingFeedback({
  pending,
  error,
  loaded,
}: {
  pending: boolean;
  error: string | null;
  loaded: boolean;
}) {
  if (!loaded || (!pending && !error)) return null;
  return (
    <span class="preference-setting-feedback" aria-live="polite" aria-atomic="true">
      {pending && <span role="status">Saving…</span>}
      {!pending && error && <span role="alert">{error}</span>}
    </span>
  );
}
