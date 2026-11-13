import type { ComponentChildren, RefObject } from "preact";
import { useEffect, useMemo, useRef, useState } from "preact/hooks";
import { RequestError } from "../http/RequestError.ts";
import { DirectoryClient } from "../files/DirectoryClient.ts";
import type { DirectoryListing } from "../files/DirectoryClient.ts";
import { DirectoryTree } from "./DirectoryTree.tsx";
import { Popover } from "./Popover.tsx";
import "./folder-picker.css";

export interface FolderPickerProps {
  anchor: RefObject<HTMLElement | null>;
  initialPath: string | null;
  onChoose(path: string): Promise<boolean>;
  onClose(returnFocus: boolean): void;
  title?: string;
  busy?: boolean;
  feedback?: ComponentChildren;
}

export function FolderPicker({
  anchor,
  initialPath,
  onChoose,
  onClose,
  title = "Working directory",
  busy = false,
  feedback,
}: FolderPickerProps) {
  const client = useMemo(() => new DirectoryClient(new URL(window.location.origin)), []);
  const pathInput = useRef<HTMLInputElement>(null);
  const saveInProgress = useRef(false);
  const closed = useRef(false);
  const mounted = useRef(true);
  const [requestedPath, setRequestedPath] = useState(initialPath);
  const [pathText, setPathText] = useState(initialPath ?? "");
  const [filter, setFilter] = useState("");
  const [listing, setListing] = useState<DirectoryListing | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [retryCount, setRetryCount] = useState(0);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const readVersion = useRef(0);
  const pathEdited = useRef(false);

  useEffect(() => {
    pathInput.current?.select();
    mounted.current = true;
    return () => {
      mounted.current = false;
      closed.current = true;
    };
  }, []);

  useEffect(() => {
    if (!busy && document.activeElement === document.body)
      pathInput.current?.focus({ preventScroll: true });
  }, [busy]);

  useEffect(() => {
    const controller = new AbortController();
    const version = readVersion.current;
    setLoading(true);
    setError(null);
    setListing(null);

    void client
      .list(requestedPath, filter, controller.signal)
      .then((result) => {
        if (controller.signal.aborted || version !== readVersion.current) return;
        setListing(result);
        if (!pathEdited.current) setPathText(result.path ?? "");
      })
      .catch((cause: unknown) => {
        if (!controller.signal.aborted && version === readVersion.current)
          setError(readFailure(cause));
      })
      .finally(() => {
        if (!controller.signal.aborted && version === readVersion.current) setLoading(false);
      });

    return () => controller.abort();
  }, [client, filter, requestedPath, retryCount]);

  const prepareRead = () => {
    readVersion.current++;
    setLoading(true);
    setError(null);
    setListing(null);
  };
  const navigate = (path: string | null) => {
    if (saving || busy) return;
    prepareRead();
    pathEdited.current = false;
    pathInput.current?.focus({ preventScroll: true });
    setPathText(path ?? "");
    setFilter("");
    if (requestedPath === path) setRetryCount((count) => count + 1);
    else setRequestedPath(path);
  };
  const changeFilter = (value: string) => {
    if (saving || busy || filter === value) return;
    prepareRead();
    setFilter(value);
  };
  const retry = () => {
    if (saving || busy) return;
    prepareRead();
    setRetryCount((count) => count + 1);
  };
  const close = (returnFocus: boolean) => {
    if (closed.current) return;
    closed.current = true;
    onClose(returnFocus);
  };
  const save = async () => {
    if (saveInProgress.current || busy || !pathText.trim()) return;
    saveInProgress.current = true;
    setSaving(true);
    setSaveError(null);

    try {
      const saved = await onChoose(pathText);
      if (!mounted.current || closed.current) return;
      saveInProgress.current = false;
      setSaving(false);
      if (saved) close(true);
    } catch {
      if (!mounted.current || closed.current) return;
      saveInProgress.current = false;
      setSaving(false);
      setSaveError("Could not save this folder. Try again.");
    }
  };

  const saveUnavailable = saving || busy || !pathText.trim();
  const canNavigate = !saving && !busy;

  return (
    <Popover
      anchor={anchor}
      class="folder-picker"
      label={title}
      placement="center"
      initialFocus={pathInput}
      onClose={close}
    >
      <header class="folder-picker-heading">
        <h2>{title}</h2>
      </header>
      <form
        class="folder-picker-path"
        onSubmit={(event) => {
          event.preventDefault();
          if (canNavigate) navigate(pathText || null);
        }}
      >
        <label>
          <span>Path</span>
          <input
            ref={pathInput}
            type="text"
            autoComplete="off"
            spellcheck={false}
            value={pathText}
            readOnly={!canNavigate}
            onInput={(event) => {
              pathEdited.current = true;
              setPathText(event.currentTarget.value);
            }}
          />
        </label>
        <button type="submit" aria-label="Open folder" disabled={!canNavigate}>
          <svg aria-hidden="true" viewBox="0 0 24 24">
            <path d="M5 12h14m-6-6 6 6-6 6" />
          </svg>
        </button>
      </form>
      <div class="folder-picker-toolbar">
        <nav class="folder-picker-navigation" aria-label="Folder navigation">
          <button
            type="button"
            aria-label="Parent folder"
            onClick={() => listing?.parentPath && navigate(listing.parentPath)}
            disabled={!canNavigate || !listing?.parentPath}
          >
            <svg aria-hidden="true" viewBox="0 0 24 24">
              <path d="M12 19V5m-7 7 7-7 7 7" />
            </svg>
            <span>Up</span>
          </button>
          <button
            class="folder-picker-locations"
            type="button"
            onClick={() => navigate(null)}
            disabled={!canNavigate || (loading && requestedPath === null)}
          >
            Locations
          </button>
        </nav>
        <input
          class="folder-picker-filter"
          type="search"
          maxLength={256}
          aria-label="Filter folders"
          placeholder="Filter folders"
          value={filter}
          disabled={!canNavigate}
          onInput={(event) => changeFilter(event.currentTarget.value)}
        />
      </div>
      {listing?.truncated && (
        <p class="folder-picker-guidance">Listing is incomplete. Filter folders to narrow it.</p>
      )}
      <div class="folder-picker-list" role="region" aria-label="Subfolders" aria-busy={loading}>
        {loading && <p aria-live="polite">Loading folders…</p>}
        {!loading && error && (
          <div class="folder-picker-error">
            <p role="alert">{error}</p>
            <button type="button" onClick={retry} disabled={!canNavigate}>
              Retry
            </button>
          </div>
        )}
        {!loading && listing && listing.directories.length === 0 && (
          <p>{filter ? "No matching folders." : "No folders."}</p>
        )}
        {!loading && listing && (
          <DirectoryTree
            listing={listing}
            client={client}
            disabled={!canNavigate}
            selectedPath={pathText || null}
            onSelect={(path) => {
              pathEdited.current = true;
              setPathText(path);
            }}
            onOpen={navigate}
          />
        )}
      </div>
      {(feedback != null || saveError) && (
        <div class="folder-picker-feedback" aria-live="polite">
          {feedback}
          {saveError && <p role="alert">{saveError}</p>}
        </div>
      )}
      <footer class="folder-picker-actions">
        <button type="button" onClick={() => close(true)}>
          Cancel
        </button>
        <button
          class="folder-picker-save"
          type="button"
          aria-disabled={saveUnavailable}
          onClick={() => void save()}
        >
          {saving ? "Saving…" : "Save"}
        </button>
      </footer>
    </Popover>
  );
}

function readFailure(cause: unknown): string {
  if (cause instanceof RequestError) {
    const body = cause.responseBody as { error?: unknown } | null;
    if (typeof body?.error === "string" && body.error.trim()) return body.error;
    return `Folder listing failed (HTTP ${cause.status}).`;
  }
  return "Folders could not be loaded.";
}
