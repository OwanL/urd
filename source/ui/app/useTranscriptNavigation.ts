import { useCallback, useEffect, useMemo, useRef, useState } from "preact/hooks";
import {
  DebouncedTranscriptNavigationWriter,
  TranscriptNavigationStorage,
  type NavigationStorageResult,
  type TranscriptNavigationState,
  type TranscriptNavigationStorageLike,
} from "../sessions/TranscriptNavigationStorage.ts";

export const transcriptNavigationProfileVersion = "weighted-source-v2";

const localStorageLike: TranscriptNavigationStorageLike = {
  getItem(key) {
    return window.localStorage.getItem(key);
  },
  setItem(key, value) {
    window.localStorage.setItem(key, value);
  },
  removeItem(key) {
    window.localStorage.removeItem(key);
  },
};

export interface TranscriptNavigationStore {
  load(width: number): NavigationStorageResult<{
    state: TranscriptNavigationState | null;
    pixelReuse: boolean;
  }>;
  save(state: TranscriptNavigationState): void;
  flush(): void;
}

export function useTranscriptNavigation(
  sessionId: string,
  initial: TranscriptNavigationState | undefined,
  onChange: (state: TranscriptNavigationState) => void,
  onSuspend: () => void,
  onResume: () => void,
): TranscriptNavigationStore {
  const [storage] = useState(() => new TranscriptNavigationStorage(localStorageLike));
  const [writer] = useState(
    () =>
      new DebouncedTranscriptNavigationWriter(
        storage,
        (callback) => window.setTimeout(callback, 160),
        (handle) => window.clearTimeout(handle as number),
      ),
  );
  const callbacks = useRef({ onChange, onSuspend, onResume });
  callbacks.current = { onChange, onSuspend, onResume };
  const loaded = useRef(false);
  const initialState = useRef(initial);

  const load = useCallback(
    (width: number) => {
      if (loaded.current) {
        const state = initialState.current ?? null;
        return {
          value: {
            state,
            pixelReuse:
              state !== null &&
              state.width === width &&
              state.profileVersion === transcriptNavigationProfileVersion,
          },
          error: null,
        };
      }
      loaded.current = true;
      if (initialState.current !== undefined) {
        const state = initialState.current;
        return {
          value: {
            state,
            pixelReuse:
              state.width === width && state.profileVersion === transcriptNavigationProfileVersion,
          },
          error: null,
        };
      }
      const result = storage.load(sessionId, {
        width,
        profileVersion: transcriptNavigationProfileVersion,
      });
      initialState.current = result.value.state ?? undefined;
      return result;
    },
    [sessionId, storage],
  );

  const save = useCallback(
    (state: TranscriptNavigationState) => {
      initialState.current = state;
      loaded.current = true;
      callbacks.current.onChange(state);
      writer.scheduleWrite(sessionId, state);
    },
    [sessionId, writer],
  );
  const flush = useCallback(() => writer.flush(), [writer]);

  useEffect(() => {
    let suspended = false;
    const suspend = () => {
      if (suspended) return;
      suspended = true;
      writer.flush();
      callbacks.current.onSuspend();
    };
    const resume = () => {
      if (document.hidden) return;
      if (suspended) {
        suspended = false;
        callbacks.current.onResume();
      }
    };
    const visibilityChanged = () => (document.hidden ? suspend() : resume());
    const freeze = () => suspend();
    const thaw = () => resume();

    window.addEventListener("pagehide", suspend);
    window.addEventListener("pageshow", resume);
    document.addEventListener("visibilitychange", visibilityChanged);
    document.addEventListener("freeze", freeze as EventListener);
    document.addEventListener("resume", thaw as EventListener);
    return () => {
      writer.flush();
      window.removeEventListener("pagehide", suspend);
      window.removeEventListener("pageshow", resume);
      document.removeEventListener("visibilitychange", visibilityChanged);
      document.removeEventListener("freeze", freeze as EventListener);
      document.removeEventListener("resume", thaw as EventListener);
    };
  }, [writer]);

  return useMemo(() => ({ load, save, flush }), [flush, load, save]);
}
