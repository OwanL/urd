import type { ComponentChildren, RefObject } from "preact";
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "preact/hooks";
import { RequestError } from "../http/RequestError.ts";
import { DirectoryClient } from "./DirectoryClient.ts";
import type { DirectoryListing } from "./DirectoryClient.ts";
import { DirectoryList } from "./DirectoryList.tsx";
import { folderBreadcrumbs } from "./FolderPath.ts";
import { Popover } from "../controls/Popover.tsx";
import "./folder-picker.css";

export interface FolderPickerProps {
  anchor: RefObject<HTMLElement | null>;
  initialPath: string | null;
  onChoose(path: string): Promise<boolean>;
  onClose(returnFocus: boolean): void;
  onDraftChange?(): void;
  title?: string;
  busy?: boolean;
  feedback?: ComponentChildren;
}

export function FolderPicker({
  anchor,
  initialPath,
  onChoose,
  onClose,
  onDraftChange,
  title = "Working directory",
  busy = false,
  feedback,
}: FolderPickerProps) {
  const client = useMemo(() => new DirectoryClient(new URL(window.location.origin)), []);
  const pathInput = useRef<HTMLInputElement>(null);
  const breadcrumbs = useRef<HTMLElement>(null);
  const listRef = useRef<HTMLDivElement>(null);
  const focusAfterRead = useRef<Element | null>(null);
  const saveInProgress = useRef(false);
  const closed = useRef(false);
  const mounted = useRef(true);
  const readVersion = useRef(0);
  const [request, setRequest] = useState({ path: initialPath, filter: "", version: 0 });
  const [address, setAddress] = useState(initialPath ?? "");
  const [editingAddress, setEditingAddress] = useState(false);
  const [selectedPath, setSelectedPath] = useState<string | null>(null);
  const [listing, setListing] = useState<DirectoryListing | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
      closed.current = true;
    };
  }, []);

  useLayoutEffect(() => {
    if (editingAddress) {
      pathInput.current?.focus({ preventScroll: true });
      pathInput.current?.select();
    }
  }, [editingAddress]);

  useLayoutEffect(() => {
    const nav = breadcrumbs.current;
    if (!nav || editingAddress) return;
    const revealLocation = () => {
      nav.scrollLeft = nav.scrollWidth;
    };
    revealLocation();
    const observer = new ResizeObserver(revealLocation);
    observer.observe(nav);
    return () => observer.disconnect();
  }, [listing?.path, editingAddress]);

  useLayoutEffect(() => {
    if (loading) return;
    const origin = focusAfterRead.current;
    focusAfterRead.current = null;
    const active = document.activeElement;
    if (
      !error &&
      (active?.classList.contains("folder-picker") ||
        (origin && (active === origin || active === document.body)))
    )
      listRef.current?.focus({ preventScroll: true });
  }, [listing, loading, error]);

  useEffect(() => {
    const controller = new AbortController();
    const version = request.version;
    void client
      .list(request.path, request.filter, controller.signal)
      .then((result) => {
        if (controller.signal.aborted || version !== readVersion.current) return;
        setListing(result);
        setAddress(result.path ?? "");
        setEditingAddress(false);
      })
      .catch((cause: unknown) => {
        if (!controller.signal.aborted && version === readVersion.current)
          setError(readFailure(cause));
      })
      .finally(() => {
        if (!controller.signal.aborted && version === readVersion.current) setLoading(false);
      });
    return () => controller.abort();
  }, [client, request]);

  const clearFeedback = () => {
    setSaveError(null);
    onDraftChange?.();
  };
  const read = (path: string | null, filter: string, restoreFocus: boolean) => {
    if (saving || busy) return;
    focusAfterRead.current = restoreFocus ? document.activeElement : null;
    const version = ++readVersion.current;
    setLoading(true);
    setError(null);
    setSelectedPath(null);
    clearFeedback();
    setRequest({ path, filter, version });
  };
  const navigate = (path: string | null) => read(path, "", true);
  const close = (returnFocus: boolean) => {
    if (closed.current) return;
    closed.current = true;
    onClose(returnFocus);
  };
  const candidate = selectedPath ?? listing?.path ?? null;
  const addressDirty = editingAddress && address !== (listing?.path ?? "");
  const saveUnavailable = saving || busy || loading || !!error || addressDirty || !candidate;
  const canNavigate = !saving && !busy;

  const save = async () => {
    if (saveInProgress.current || saveUnavailable || !candidate) return;
    saveInProgress.current = true;
    setSaving(true);
    setSaveError(null);
    try {
      const saved = await onChoose(candidate);
      if (!mounted.current || closed.current) return;
      saveInProgress.current = false;
      setSaving(false);
      if (saved) close(true);
    } catch {
      if (!mounted.current || closed.current) return;
      saveInProgress.current = false;
      setSaving(false);
      setSaveError(
        "Folder selection could not be confirmed. Check the current directory before trying again.",
      );
    }
  };

  return (
    <Popover
      anchor={anchor}
      class="folder-picker"
      label={title}
      placement="center"
      initialFocus={listRef}
      onClose={close}
    >
      <header class="folder-picker-heading">
        <h2>{title}</h2>
      </header>
      <div class="folder-picker-location">
        <button
          type="button"
          aria-label="Parent folder"
          title="Parent folder"
          disabled={!canNavigate || loading || !listing?.parentPath}
          onClick={() => listing?.parentPath && navigate(listing.parentPath)}
        >
          <svg aria-hidden="true" viewBox="0 0 24 24">
            <path d="M12 19V5m-7 7 7-7 7 7" />
          </svg>
        </button>
        {editingAddress ? (
          <form
            onSubmit={(event) => {
              event.preventDefault();
              if (canNavigate && address.trim()) navigate(address);
            }}
          >
            <input
              ref={pathInput}
              aria-label="Folder address"
              autoComplete="off"
              spellcheck={false}
              value={address}
              readOnly={!canNavigate || loading}
              onInput={(event) => {
                setAddress(event.currentTarget.value);
                clearFeedback();
              }}
            />
            <button
              type="submit"
              aria-label="Open folder"
              disabled={!canNavigate || loading || !address.trim()}
            >
              <svg aria-hidden="true" viewBox="0 0 24 24">
                <path d="M5 12h14m-6-6 6 6-6 6" />
              </svg>
            </button>
          </form>
        ) : (
          <>
            <nav ref={breadcrumbs} class="folder-picker-breadcrumbs" aria-label="Folder location">
              <button
                type="button"
                disabled={!canNavigate || loading}
                onClick={() => navigate(null)}
              >
                Locations
              </button>
              {listing?.path &&
                folderBreadcrumbs(listing.path).map((crumb, index, crumbs) => (
                  <span key={crumb.path}>
                    <span aria-hidden="true">›</span>
                    <button
                      type="button"
                      aria-current={index === crumbs.length - 1 ? "location" : undefined}
                      title={crumb.path}
                      disabled={!canNavigate || loading}
                      onClick={() => navigate(crumb.path)}
                    >
                      {crumb.name}
                    </button>
                  </span>
                ))}
            </nav>
            <button
              class="folder-picker-type-path"
              type="button"
              aria-label="Type a folder path"
              disabled={!canNavigate || loading}
              onClick={() => setEditingAddress(true)}
            >
              Type path
            </button>
          </>
        )}
      </div>
      <input
        class="folder-picker-filter"
        type="search"
        aria-label="Filter folders"
        placeholder="Filter folders"
        maxLength={256}
        value={request.filter}
        disabled={
          !canNavigate || !listing || editingAddress || (loading && request.path !== listing.path)
        }
        onInput={(event) => read(listing?.path ?? null, event.currentTarget.value, false)}
      />
      {listing?.truncated && (
        <p class="folder-picker-guidance">More folders available. Filter to narrow the list.</p>
      )}
      <div class="folder-picker-list" aria-busy={loading}>
        {loading && <p role="status">Loading folders…</p>}
        {error && (
          <div class="folder-picker-error">
            <p role="alert">{error}</p>
            <button
              type="button"
              disabled={!canNavigate}
              onClick={() => read(request.path, request.filter, true)}
            >
              Retry
            </button>
          </div>
        )}
        {listing && (
          <DirectoryList
            key={request.version}
            listRef={listRef}
            directories={listing.directories}
            selectedPath={selectedPath}
            emptyMessage={
              loading ? undefined : request.filter ? "No matching folders." : "No subfolders."
            }
            disabled={!canNavigate || loading || !!error}
            onSelect={(path) => {
              if (!canNavigate || loading || error) return;
              setSelectedPath(path);
              clearFeedback();
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
      <div class="folder-picker-selection">
        <span>Folder</span>
        <code title={candidate ?? undefined}>
          {candidate
            ? (folderBreadcrumbs(candidate).at(-1)?.name ?? candidate)
            : "Select a location"}
        </code>
      </div>
      <footer class="folder-picker-actions">
        {selectedPath && (
          <button
            class="folder-picker-open"
            type="button"
            disabled={!canNavigate || loading}
            onClick={() => navigate(selectedPath)}
          >
            Open
          </button>
        )}
        <button type="button" onClick={() => close(true)}>
          Cancel
        </button>
        <button
          class="folder-picker-save"
          type="button"
          aria-disabled={saveUnavailable}
          onClick={() => void save()}
        >
          {saving ? "Selecting…" : "Select folder"}
        </button>
      </footer>
    </Popover>
  );
}

function readFailure(cause: unknown): string {
  if (cause instanceof RequestError) {
    const body = cause.responseBody as { error?: unknown } | null;
    if (typeof body?.error === "string" && body.error.trim()) return body.error;
    if (cause.status === 400 || cause.status === 404) return "Folder was not found.";
    if (cause.status === 403) return "Access to this folder was denied.";
  }
  return "Folders could not be loaded.";
}
