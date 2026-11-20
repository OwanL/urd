import { useCallback, useEffect, useMemo, useRef, useState } from "preact/hooks";
import {
  DebouncedTranscriptNavigationWriter,
  TranscriptNavigationStorage,
  type NavigationStorageResult,
  type TranscriptNavigationState,
  type TranscriptNavigationStorageLike,
} from "./TranscriptNavigationStorage.ts";

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

const closedSessions = new Set<string>();
const storage = new TranscriptNavigationStorage(localStorageLike);

export function forgetTranscriptNavigation(sessionId: string): void {
  closedSessions.add(sessionId);
  const result = storage.clear(sessionId);
  if (result.error !== null) throw new Error(result.error);
}

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
  const [writer] = useState(
    () =>
      new DebouncedTranscriptNavigationWriter(
        storage,
        (callback) => window.setTimeout(callback, 160),
        (handle) => window.clearTimeout(handle as number),
        (id) => !closedSessions.has(id),
      ),
  );
  const callbacks = useRef({ onChange, onSuspend, onResume });
  callbacks.current = { onChange, onSuspend, onResume };
  const loaded = useRef(false);
  const initialState = useRef(initial);

  const load = useCallback(
    (width: number) => {
      if (closedSessions.has(sessionId))
        return { value: { state: null, pixelReuse: false }, error: null };
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
    [sessionId],
  );

  const save = useCallback(
    (state: TranscriptNavigationState) => {
      if (closedSessions.has(sessionId)) return;
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
      flush();
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
      flush();
      window.removeEventListener("pagehide", suspend);
      window.removeEventListener("pageshow", resume);
      document.removeEventListener("visibilitychange", visibilityChanged);
      document.removeEventListener("freeze", freeze as EventListener);
      document.removeEventListener("resume", thaw as EventListener);
    };
  }, [flush]);

  return useMemo(() => ({ load, save, flush }), [flush, load, save]);
}
