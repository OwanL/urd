import type { RefObject } from "preact";
import { useEffect, useMemo, useRef, useState } from "preact/hooks";
import { RequestError } from "../http/RequestError.ts";
import { DirectoryClient } from "../files/DirectoryClient.ts";
import type { DirectoryListing } from "../files/DirectoryClient.ts";
import { Popover } from "./Popover.tsx";
import "./folder-picker.css";

export interface FolderPickerProps {
  anchor: RefObject<HTMLElement | null>;
  initialPath: string | null;
  onChoose(path: string): void;
  onClose(returnFocus: boolean): void;
}

export function FolderPicker({ anchor, initialPath, onChoose, onClose }: FolderPickerProps) {
  const client = useMemo(() => new DirectoryClient(new URL(window.location.origin)), []);
  const pathInput = useRef<HTMLInputElement>(null);
  const [requestedPath, setRequestedPath] = useState(initialPath);
  const [pathText, setPathText] = useState(initialPath ?? "");
  const [filter, setFilter] = useState("");
  const [listing, setListing] = useState<DirectoryListing | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [retryCount, setRetryCount] = useState(0);
  const readVersion = useRef(0);
  const pathEdited = useRef(false);

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
    prepareRead();
    pathEdited.current = false;
    setPathText(path ?? "");
    setFilter("");
    if (requestedPath === path) setRetryCount((count) => count + 1);
    else setRequestedPath(path);
  };
  const changeFilter = (value: string) => {
    if (filter === value) return;
    prepareRead();
    setFilter(value);
  };
  const retry = () => {
    prepareRead();
    setRetryCount((count) => count + 1);
  };
  const choose = (path: string) => {
    onClose(true);
    onChoose(path);
  };

  return (
    <Popover
      anchor={anchor}
      class="folder-picker"
      label="Choose folder"
      placement="center"
      initialFocus={pathInput}
      onClose={onClose}
    >
      <header class="folder-picker-heading">
        <h2>Choose folder</h2>
      </header>
      <form
        class="folder-picker-path"
        onSubmit={(event) => {
          event.preventDefault();
          navigate(pathText || null);
        }}
      >
        <label>
          Path
          <input
            ref={pathInput}
            type="text"
            autoComplete="off"
            spellcheck={false}
            value={pathText}
            onInput={(event) => {
              pathEdited.current = true;
              setPathText(event.currentTarget.value);
            }}
          />
        </label>
        <button type="submit">Go</button>
      </form>
      <nav class="folder-picker-navigation" aria-label="Folder navigation">
        <button
          type="button"
          onClick={() => navigate(null)}
          disabled={loading && requestedPath === null}
        >
          Locations
        </button>
        <button
          type="button"
          onClick={() => listing?.parentPath && navigate(listing.parentPath)}
          disabled={loading || !listing?.parentPath}
        >
          Parent folder
        </button>
      </nav>
      <label class="folder-picker-filter">
        Filter folders
        <input
          type="search"
          maxLength={256}
          value={filter}
          onInput={(event) => changeFilter(event.currentTarget.value)}
        />
      </label>
      {listing?.truncated && (
        <p class="folder-picker-guidance">Listing is incomplete. Filter folders to narrow it.</p>
      )}
      <div class="folder-picker-list" role="region" aria-label="Subfolders" aria-busy={loading}>
        {loading && <p aria-live="polite">Loading folders…</p>}
        {!loading && error && (
          <div class="folder-picker-error">
            <p role="alert">{error}</p>
            <button type="button" onClick={retry}>
              Retry
            </button>
          </div>
        )}
        {!loading && listing && listing.directories.length === 0 && <p>No subfolders.</p>}
        {!loading && listing && listing.directories.length > 0 && (
          <ul>
            {listing.directories.map((directory) => (
              <li key={directory.path}>
                <button type="button" onClick={() => navigate(directory.path)}>
                  {directory.name}
                </button>
              </li>
            ))}
          </ul>
        )}
      </div>
      <footer class="folder-picker-actions">
        <button
          type="button"
          onClick={() => listing?.path && choose(listing.path)}
          disabled={loading || pathEdited.current || !listing?.path}
        >
          Use folder
        </button>
        <button type="button" onClick={() => onClose(true)}>
          Cancel
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
