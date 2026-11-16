import { useCallback, useEffect, useRef, useState } from "preact/hooks";
import { RequestError } from "../http/RequestError.ts";
import type { PreferencesState } from "../preferences/usePreferences.ts";
import type {
  ProtectedResourceCategory,
  SelectableResource,
  SelectableResources,
} from "../preferences/PreferencesClient.ts";

const noItems: readonly string[] = [];
const categories: readonly ProtectedResourceCategory[] = ["tools", "skills", "instructions"];
const categoryNames: Record<ProtectedResourceCategory, string> = {
  tools: "Tools",
  skills: "Skills",
  instructions: "Instructions",
};

export function ProtectedResourcesSettings({
  state,
  active,
}: {
  state: PreferencesState;
  active: boolean;
}) {
  const [resources, setResources] = useState<SelectableResources | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const readAbort = useRef<AbortController | null>(null);

  const { getSelectableResources } = state;
  const readResources = useCallback(() => {
    readAbort.current?.abort();
    const abort = new AbortController();
    readAbort.current = abort;
    setLoading(true);
    setError(null);
    void getSelectableResources(abort.signal)
      .then((result) => {
        if (readAbort.current !== abort || abort.signal.aborted) return;
        setResources(result);
      })
      .catch((cause: unknown) => {
        if (readAbort.current !== abort || abort.signal.aborted) return;
        setError(resourceError(cause));
      })
      .finally(() => {
        if (readAbort.current === abort && !abort.signal.aborted) setLoading(false);
      });
  }, [getSelectableResources]);

  useEffect(() => {
    if (!active) return;
    readResources();
    return () => {
      readAbort.current?.abort();
      readAbort.current = null;
    };
  }, [active, readResources, state.preferences?.workingDirectory]);

  return (
    <section class="preference-protected-settings" aria-label="Protected resources">
      <h3>Protected resources</h3>
      <p class="general-settings-description">
        Checked items are protected. Uncheck to remove protection.
      </p>
      {!state.loaded && <p role="status">Loading preferences…</p>}
      {state.loaded && (
        <>
          {loading && <p role="status">Loading selectable resources…</p>}
          {error && (
            <div class="general-settings-feedback">
              <p role="alert">{error}</p>
              <button type="button" disabled={loading} onClick={readResources}>
                Retry
              </button>
            </div>
          )}
          {categories.map((category) => (
            <ProtectedCategory
              key={category}
              category={category}
              state={state}
              resources={resources?.[category] ?? []}
              catalogLoaded={resources !== null}
            />
          ))}
        </>
      )}
    </section>
  );
}

function ProtectedCategory({
  category,
  state,
  resources,
  catalogLoaded,
}: {
  category: ProtectedResourceCategory;
  state: PreferencesState;
  resources: readonly SelectableResource[];
  catalogLoaded: boolean;
}) {
  const saved = state.preferences?.protectedResources[category] ?? noItems;
  const [draft, setDraft] = useState<readonly string[]>(saved);
  const preferenceKey = `protectedResources:${category}` as const;

  useEffect(() => setDraft(saved), [saved]);

  const names = new Map(resources.map((resource) => [resource.id, resource.name]));
  const entries: SelectableResource[] = [
    ...resources,
    ...draft
      .filter((id) => !names.has(id))
      .map((id) => ({
        id,
        name: catalogLoaded ? "Unavailable saved item" : "Saved item",
      })),
  ];
  const saveItems = async (id: string, protect: boolean) => {
    state.clearError();
    const next = protect ? [...draft, id] : draft.filter((item) => item !== id);
    setDraft(next);
    if (!(await state.saveProtectedItems(category, next))) setDraft(saved);
  };

  return (
    <fieldset class="preference-protected-category">
      <legend>{categoryNames[category]}</legend>
      {entries.length === 0 && catalogLoaded && <p>No selectable {category}.</p>}
      {entries.length === 0 && !catalogLoaded && !state.pending && (
        <p>Selectable resources have not loaded.</p>
      )}
      <div class="preference-protected-list">
        {entries.map((resource) => (
          <label class="preference-protected-item" key={resource.id}>
            <input
              type="checkbox"
              aria-label={`Protect ${resource.name}`}
              checked={draft.includes(resource.id)}
              disabled={state.pending}
              onChange={(event) => void saveItems(resource.id, event.currentTarget.checked)}
            />
            <span>{resource.name}</span>
            <code>{resource.id}</code>
          </label>
        ))}
      </div>
      {state.pendingFor === preferenceKey && (
        <p class="preference-setting-feedback" role="status">
          Saving…
        </p>
      )}
      {state.loaded && state.errorFor(preferenceKey) && (
        <p class="preference-setting-feedback preference-setting-error" role="alert">
          {state.errorFor(preferenceKey)}
        </p>
      )}
    </fieldset>
  );
}

function resourceError(cause: unknown): string {
  if (cause instanceof RequestError) {
    const body = cause.responseBody as { error?: unknown } | null;
    if (typeof body?.error === "string" && body.error.trim()) return body.error;
    return `Selectable resources could not be loaded (HTTP ${cause.status}).`;
  }
  return "Selectable resources could not be loaded.";
}
