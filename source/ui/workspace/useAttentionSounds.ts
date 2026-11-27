import { useCallback, useEffect, useRef, useState } from "preact/hooks";
import type { AttentionKind } from "../sessions/SessionEvents.ts";
import { AttentionChimes } from "./AttentionChimes.ts";

const preferenceKey = "urd.attention-sounds.v1";

function readEnabled(): boolean {
  try {
    return localStorage.getItem(preferenceKey) !== "off";
  } catch {
    return true;
  }
}

export function useAttentionSounds() {
  const [enabled, setEnabled] = useState(readEnabled);
  const current = useRef(enabled);
  const [chimes] = useState(() => new AttentionChimes());
  const applyEnabled = useCallback(
    (next: boolean) => {
      current.current = next;
      setEnabled(next);
      if (!next) chimes.dispose();
    },
    [chimes],
  );
  useEffect(() => {
    function unlock(event: Event) {
      if (event.isTrusted && current.current) chimes.unlock();
    }
    function sync(event: StorageEvent) {
      if (event.key === null || event.key === preferenceKey) applyEnabled(readEnabled());
    }
    window.addEventListener("pointerup", unlock, true);
    window.addEventListener("keydown", unlock, true);
    window.addEventListener("storage", sync);
    return () => {
      window.removeEventListener("pointerup", unlock, true);
      window.removeEventListener("keydown", unlock, true);
      window.removeEventListener("storage", sync);
      chimes.dispose();
    };
  }, [applyEnabled, chimes]);

  const play = useCallback(
    (kind: AttentionKind) => {
      if (current.current) chimes.play(kind);
    },
    [chimes],
  );
  return {
    enabled,
    setEnabled(next: boolean) {
      applyEnabled(next);
      if (next) chimes.unlock();
      try {
        if (next) localStorage.removeItem(preferenceKey);
        else localStorage.setItem(preferenceKey, "off");
      } catch {
        // Apply in this page even if browser storage is unavailable.
      }
    },
    play,
  };
}
