import { createContext, type ComponentChildren } from "preact";
import { useCallback, useEffect, useMemo, useRef, useState } from "preact/hooks";
import type { ModelFavorite } from "../../preferences/PreferencesClient.ts";

interface ModelFavoritesPreferences {
  readonly preferences: { readonly modelFavorites: readonly ModelFavorite[] } | null;
  readonly loaded: boolean;
  readonly pending: boolean;
  readonly pendingFor: string | null;
  saveModelFavorites(favorites: readonly ModelFavorite[]): Promise<boolean>;
}

interface ModelFavoritesValue {
  readonly keys: ReadonlySet<string>;
  readonly disabled: boolean;
  toggle(favorite: ModelFavorite): void;
}

const emptyKeys = new Set<string>();
const emptyFavorites: readonly ModelFavorite[] = [];

export const ModelFavoritesContext = createContext<ModelFavoritesValue>({
  keys: emptyKeys,
  disabled: true,
  toggle: () => {},
});

export function modelFavoriteKey(favorite: ModelFavorite): string {
  return `${favorite.connectionId}/${favorite.modelId}`;
}

export function ModelFavoritesProvider({
  preferences,
  children,
}: {
  preferences: ModelFavoritesPreferences;
  children: ComponentChildren;
}) {
  const saved = preferences.preferences?.modelFavorites ?? emptyFavorites;
  const savedRef = useRef(saved);
  savedRef.current = saved;
  const [favorites, setFavorites] = useState<readonly ModelFavorite[]>(saved);
  const current = useRef(favorites);
  current.current = favorites;
  const saving = useRef(false);

  useEffect(() => {
    if (preferences.pendingFor === "modelFavorites" || saving.current) return;
    current.current = saved;
    setFavorites(saved);
  }, [preferences.pendingFor, saved]);

  const disabled =
    !preferences.loaded || (preferences.pending && preferences.pendingFor !== "modelFavorites");
  const saveQueued = useCallback(async () => {
    if (saving.current) return;
    saving.current = true;
    while (true) {
      const requested = current.current;
      if (!(await preferences.saveModelFavorites(requested))) {
        current.current = savedRef.current;
        setFavorites(savedRef.current);
        break;
      }
      if (current.current === requested) break;
    }
    saving.current = false;
  }, [preferences]);
  const toggle = useCallback(
    (favorite: ModelFavorite) => {
      if (disabled) return;
      const key = modelFavoriteKey(favorite);
      const next = current.current.some((item) => modelFavoriteKey(item) === key)
        ? current.current.filter((item) => modelFavoriteKey(item) !== key)
        : [...current.current, favorite];
      current.current = next;
      setFavorites(next);
      void saveQueued();
    },
    [disabled, saveQueued],
  );
  const keys = useMemo(() => new Set(favorites.map(modelFavoriteKey)), [favorites]);

  return (
    <ModelFavoritesContext.Provider value={{ keys, disabled, toggle }}>
      {children}
    </ModelFavoritesContext.Provider>
  );
}
