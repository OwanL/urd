import { useCallback, useEffect, useMemo, useRef, useState } from "preact/hooks";
import { requestJson } from "../http/Requests.ts";
import {
  buildSourceTree,
  filenameKey,
  pathKey,
  setSourceIgnored,
  sourceSelection,
  type SourceNode,
} from "./sourceTree.ts";
import "./sourceIgnorePicker.css";

type Kind = "instructions" | "skills";
interface Rules {
  paths: string[];
  includes: string[];
  filenames: string[];
}
interface Preferences {
  sourceIgnores: Record<Kind | "filenames", string[]> & {
    instructionIncludes?: string[];
    skillIncludes?: string[];
  };
}
interface Preview {
  paths: string[];
  filenames: string[];
  workingDirectory: string;
  eligibleCount: number;
  estimatedTokens: number;
  partial: boolean;
}

const url = (path: string) => new URL(path, window.location.href);
const preview = (kind: Kind, rules: Rules, signal?: AbortSignal) =>
  requestJson<Preview>(
    url("/api/preferences/source-preview"),
    "POST",
    { category: kind, rules },
    signal,
  );

export function SourceIgnorePicker({
  kind,
  active = true,
  onBusyChange,
}: {
  kind: Kind;
  active?: boolean;
  onBusyChange?: (busy: boolean) => void;
}) {
  return <Picker key={kind} kind={kind} active={active} onBusyChange={onBusyChange} />;
}

function Picker({
  kind,
  active,
  onBusyChange,
}: {
  kind: Kind;
  active: boolean;
  onBusyChange: ((busy: boolean) => void) | undefined;
}) {
  const [rules, setRules] = useState<Rules | null>(null);
  const [listing, setListing] = useState<Preview | null>(null);
  const [updating, setUpdating] = useState(false);
  const [readFailed, setReadFailed] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [revision, setRevision] = useState(0);
  const [expanded, setExpanded] = useState<Set<string>>(() => new Set());
  const openedDirectory = useRef<string | null>(null);
  const generation = useRef(0);
  const saving = useRef(false);
  const callback = useRef(onBusyChange);
  callback.current = onBusyChange;

  useEffect(() => {
    if (!active) return;
    const current = ++generation.current;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let request: AbortController | null = null;
    let delay = 1000;
    const read = async () => {
      if (document.hidden || request || saving.current) return;
      const controller = new AbortController();
      request = controller;
      const isCurrent = () => !controller.signal.aborted && generation.current === current;
      try {
        const preferences = await requestJson<Preferences>(
          url("/api/preferences"),
          "GET",
          undefined,
          controller.signal,
        );
        const next = {
          paths: preferences.sourceIgnores[kind],
          includes:
            (kind === "instructions"
              ? preferences.sourceIgnores.instructionIncludes
              : preferences.sourceIgnores.skillIncludes) ?? [],
          filenames: kind === "instructions" ? preferences.sourceIgnores.filenames : [],
        };
        const selected = await preview(kind, next, controller.signal);
        if (!isCurrent()) return;
        setRules(next);
        setListing(selected);
        setUpdating(false);
        setReadFailed(false);
        if (selected.paths.length && openedDirectory.current !== selected.workingDirectory) {
          openedDirectory.current = selected.workingDirectory;
          setExpanded(
            new Set(
              buildSourceTree(selected.paths, selected.workingDirectory).map((node) =>
                pathKey(node.path),
              ),
            ),
          );
        }
        delay = selected.partial ? Math.min(delay * 2, 5000) : 30000;
      } catch {
        if (isCurrent()) setReadFailed(true);
        delay = Math.min(delay * 2, 30000);
      } finally {
        if (request === controller) request = null;
        if (isCurrent() && !document.hidden) timer = setTimeout(() => void read(), delay);
      }
    };
    const stop = () => {
      clearTimeout(timer);
      request?.abort();
      request = null;
    };
    const visibilityChanged = () => {
      stop();
      if (!document.hidden) void read();
    };
    void read();
    document.addEventListener("visibilitychange", visibilityChanged);
    return () => {
      stop();
      document.removeEventListener("visibilitychange", visibilityChanged);
    };
  }, [active, kind, revision]);

  useEffect(
    () => () => {
      callback.current?.(false);
    },
    [],
  );
  const tree = useMemo(
    () => (listing ? buildSourceTree(listing.paths, listing.workingDirectory) : []),
    [listing],
  );
  const save = useCallback(
    async (next: Rules) => {
      if (!rules || saving.current) return;
      saving.current = true;
      setBusy(true);
      callback.current?.(true);
      setError(null);
      setRules(next);
      setUpdating(true);
      generation.current++;
      try {
        await requestJson<unknown>(url("/api/preferences/source-ignores"), "POST", {
          category: kind,
          paths: next.paths,
          includes: next.includes,
          filenames: next.filenames,
        });
      } catch {
        setRules(rules);
        setError("Could not confirm the save.");
      } finally {
        saving.current = false;
        setRevision((value) => value + 1);
        setBusy(false);
        callback.current?.(false);
      }
    },
    [kind, rules],
  );

  if (!active) return null;
  function row(node: SourceNode, depth: number) {
    if (!rules) return null;
    const byName =
      node.file && rules.filenames.some((name) => filenameKey(name) === filenameKey(node.name));
    const selection = sourceSelection(node, rules, rules.filenames);
    const checked = selection === "all";
    const open = expanded.has(pathKey(node.path));
    return (
      <div key={node.path}>
        <div class="source-preview-row" style={{ paddingLeft: 8 + depth * 18 }}>
          {!node.file ? (
            <button
              class="source-preview-disclosure"
              type="button"
              aria-label={`${open ? "Collapse" : "Expand"} ${node.path}`}
              aria-expanded={open}
              onClick={() =>
                setExpanded((old) => {
                  const next = new Set(old);
                  if (open) next.delete(pathKey(node.path));
                  else next.add(pathKey(node.path));
                  return next;
                })
              }
            >
              {open ? "▾" : "▸"}
            </button>
          ) : (
            <span class="source-preview-spacer" />
          )}
          <span class="source-preview-name" title={node.path}>
            {node.name}
          </span>
          {!node.file && (
            <span class="source-preview-muted">{formatSourceCount(node.count, kind)}</span>
          )}
          {byName && <span class="source-preview-inherited">Filename</span>}
          <input
            type="checkbox"
            aria-label={`Ignore ${node.path}`}
            checked={checked}
            ref={(element) => {
              if (element) element.indeterminate = selection === "partial";
            }}
            disabled={byName || busy}
            onChange={() => void save({ ...rules, ...setSourceIgnored(rules, node, !checked) })}
          />
        </div>
        {open && node.children.map((child) => row(child, depth + 1))}
      </div>
    );
  }
  const visiblePaths = new Set<string>();
  function collect(nodes: SourceNode[]) {
    for (const node of nodes) {
      visiblePaths.add(pathKey(node.path));
      collect(node.children);
    }
  }
  collect(tree);
  const missingRoots = rules
    ? [...rules.paths, ...rules.includes].filter((path) => !visiblePaths.has(pathKey(path)))
    : [];
  const names = listing?.filenames ?? [];
  const nameCounts = new Map<string, number>();
  for (const path of listing?.paths ?? []) {
    const name = path.split(/[\\/]/).pop() ?? path;
    const key = filenameKey(name);
    nameCounts.set(key, (nameCounts.get(key) ?? 0) + 1);
  }
  return (
    <div class="source-preview">
      {error && (
        <div class="source-preview-error" role="alert">
          {error}
        </div>
      )}
      {readFailed && (
        <div class="source-preview-error" role="status">
          Could not update {kind}. Retrying…
        </div>
      )}
      {!rules || !listing ? (
        !readFailed && <div>Loading {kind}…</div>
      ) : (
        <>
          {kind === "instructions" && (
            <section class="source-preview-section" aria-label="Ignore by filename">
              <div class="source-preview-heading">
                Ignore by filename <span class="source-preview-muted">All folders</span>
              </div>
              <div class="source-preview-names">
                {names.map((name) => {
                  const count = nameCounts.get(filenameKey(name)) ?? 0;
                  return (
                    <label class="source-preview-name-option" key={name}>
                      <input
                        type="checkbox"
                        disabled={busy}
                        checked={rules.filenames.some(
                          (item) => filenameKey(item) === filenameKey(name),
                        )}
                        onChange={() =>
                          void save({
                            ...rules,
                            filenames: rules.filenames.some(
                              (item) => filenameKey(item) === filenameKey(name),
                            )
                              ? rules.filenames.filter(
                                  (item) => filenameKey(item) !== filenameKey(name),
                                )
                              : [...rules.filenames, name],
                          })
                        }
                      />
                      <span>{name}</span>
                      {count > 0 && (
                        <span class="source-preview-muted">{formatSourceCount(count, kind)}</span>
                      )}
                    </label>
                  );
                })}
              </div>
            </section>
          )}
          <section class="source-preview-section" aria-label="Ignore by location">
            <div class="source-preview-heading">
              Ignore by location <span class="source-preview-muted">Ignore</span>
            </div>
            <div class="source-preview-tree">
              {tree.length ? (
                tree.map((node) => row(node, 0))
              ) : (
                <div class="source-preview-row">No sources found.</div>
              )}
            </div>
            {missingRoots.map((path) => {
              const node: SourceNode = { name: path, path, children: [], count: 0, file: false };
              const selection = sourceSelection(node, rules, rules.filenames);
              return (
                <label class="source-preview-row" key={path}>
                  <span class="source-preview-name" title={path}>
                    {path}
                  </span>
                  <input
                    type="checkbox"
                    aria-label={`Ignore ${path}`}
                    checked={selection === "all"}
                    ref={(element) => {
                      if (element) element.indeterminate = selection === "partial";
                    }}
                    disabled={busy}
                    onChange={() =>
                      void save({ ...rules, ...setSourceIgnored(rules, node, selection !== "all") })
                    }
                  />
                </label>
              );
            })}
          </section>
          <div class="source-preview-estimate" role="status">
            {formatEstimate(listing, kind)}
            {readFailed ? " · Out of date" : listing.partial || updating ? " · Updating…" : ""}
          </div>
        </>
      )}
    </div>
  );
}

function formatSourceCount(count: number, kind: Kind): string {
  return `${count.toLocaleString()} ${count === 1 ? kind.slice(0, -1) : kind}`;
}

function formatEstimate(value: Preview, kind: Kind): string {
  return `${formatSourceCount(value.eligibleCount, kind)} eligible · ~${value.estimatedTokens.toLocaleString()} catalog tokens`;
}
