import { useCallback, useEffect, useMemo, useRef, useState } from "preact/hooks";
import { requestJson } from "../http/Requests.ts";
import { buildSourceTree, filenameKey, isWithin, pathKey, type SourceNode } from "./sourceTree.ts";
import "./sourceIgnorePicker.css";

type Kind = "instructions" | "skills";
interface Rules {
  paths: string[];
  filenames: string[];
}
interface Preferences {
  sourceIgnores: Record<Kind | "filenames", string[]>;
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
  const [summary, setSummary] = useState<Preview | null>(null);
  const [previous, setPrevious] = useState<Preview | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [revision, setRevision] = useState(0);
  const [expanded, setExpanded] = useState<Set<string>>(() => new Set());
  const generation = useRef(0);
  const saving = useRef(false);
  const callback = useRef(onBusyChange);
  callback.current = onBusyChange;

  useEffect(() => {
    if (!active) return;
    const controller = new AbortController();
    const sequence = generation;
    const current = ++sequence.current;
    setError(null);
    setRules(null);
    setListing(null);
    setSummary(null);
    setPrevious(null);
    setExpanded(new Set());
    void (async () => {
      try {
        const preferences = await requestJson<Preferences>(
          url("/api/preferences"),
          "GET",
          undefined,
          controller.signal,
        );
        const next = {
          paths: preferences.sourceIgnores[kind],
          filenames: kind === "instructions" ? preferences.sourceIgnores.filenames : [],
        };
        const selected = await preview(kind, next, controller.signal);
        if (generation.current !== current) return;
        setRules(next);
        setListing(selected);
        setSummary(selected);
        setExpanded(
          new Set(
            buildSourceTree(selected.paths, selected.workingDirectory).map((node) =>
              pathKey(node.path),
            ),
          ),
        );
      } catch {
        if (!controller.signal.aborted && generation.current === current)
          setError("Sources could not be loaded.");
      }
    })();
    return () => {
      controller.abort();
      sequence.current++;
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
      setPrevious(summary);
      setSummary(null);
      const current = generation.current;
      try {
        await requestJson<unknown>(url("/api/preferences/source-ignores"), "POST", {
          category: kind,
          paths: next.paths,
          filenames: next.filenames,
        });
        if (generation.current !== current) return;
        try {
          const result = await preview(kind, next);
          if (generation.current === current) {
            setListing(result);
            setSummary(result);
          }
        } catch {
          if (generation.current === current)
            setError("Sources were saved, but the estimate could not be loaded.");
        }
      } catch {
        if (generation.current === current) {
          setRules(rules);
          setPrevious(null);
          setError("Sources could not be saved. Changes were reverted.");
          try {
            const result = await preview(kind, rules);
            if (generation.current === current) setSummary(result);
          } catch {
            /* Retry reloads the estimate. */
          }
        }
      } finally {
        saving.current = false;
        setBusy(false);
        callback.current?.(false);
      }
    },
    [kind, rules, summary],
  );
  const refresh = useCallback(async () => {
    if (!rules || busy || saving.current) return;
    const current = generation.current;
    setBusy(true);
    callback.current?.(true);
    setError(null);
    try {
      const result = await preview(kind, rules);
      if (generation.current !== current) return;
      setListing(result);
      setSummary(result);
    } catch {
      if (generation.current === current) setError("Source list could not be refreshed.");
    } finally {
      setBusy(false);
      callback.current?.(false);
    }
  }, [busy, kind, rules]);

  if (!active) return null;
  function row(node: SourceNode, depth: number, inherited: boolean) {
    if (!rules) return null;
    const byName =
      node.file && rules.filenames.some((name) => filenameKey(name) === filenameKey(node.name));
    const locked = inherited || byName;
    const checked = locked || rules.paths.some((path) => pathKey(path) === pathKey(node.path));
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
            <span class="source-preview-muted">
              {node.count} {node.count === 1 ? "file" : "files"}
            </span>
          )}
          {locked && (
            <span class="source-preview-inherited">{inherited ? "Inherited" : "Filename"}</span>
          )}
          <input
            type="checkbox"
            aria-label={`Ignore ${node.path}`}
            checked={checked}
            disabled={locked || busy}
            onChange={() =>
              void save({
                ...rules,
                paths: checked
                  ? rules.paths.filter((path) => pathKey(path) !== pathKey(node.path))
                  : [...rules.paths, node.path],
              })
            }
          />
        </div>
        {open &&
          node.children.map((child) =>
            row(child, depth + 1, checked || rules.paths.some((path) => isWithin(node.path, path))),
          )}
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
  const missingRoots = rules?.paths.filter((path) => !visiblePaths.has(pathKey(path))) ?? [];
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
          {error}{" "}
          <button type="button" disabled={busy} onClick={() => setRevision((value) => value + 1)}>
            Retry
          </button>
        </div>
      )}
      {!rules || !listing ? (
        !error && <div>Loading sources…</div>
      ) : (
        <>
          {kind === "instructions" && (
            <section class="source-preview-section" aria-label="Ignore by filename">
              <div class="source-preview-heading">
                Ignore by filename <span class="source-preview-muted">All folders</span>
              </div>
              <div class="source-preview-names">
                {names.map((name) => (
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
                    <span class="source-preview-muted">
                      {formatFileCount(nameCounts.get(filenameKey(name)) ?? 0)}
                    </span>
                  </label>
                ))}
              </div>
            </section>
          )}
          <section class="source-preview-section" aria-label="Ignore by location">
            <div class="source-preview-heading">
              Ignore by location <span class="source-preview-muted">Ignore</span>
            </div>
            <div class="source-preview-tree">
              {tree.length ? (
                tree.map((node) =>
                  row(
                    node,
                    0,
                    rules.paths.some(
                      (path) => pathKey(path) !== pathKey(node.path) && isWithin(node.path, path),
                    ),
                  ),
                )
              ) : (
                <div class="source-preview-row">No sources found.</div>
              )}
            </div>
            {missingRoots.map((path) => (
              <label class="source-preview-row" key={path}>
                <span class="source-preview-name" title={path}>
                  {path}
                </span>
                <input
                  type="checkbox"
                  aria-label={`Ignore ${path}`}
                  checked
                  disabled={busy}
                  onChange={() =>
                    void save({
                      ...rules,
                      paths: rules.paths.filter((item) => pathKey(item) !== pathKey(path)),
                    })
                  }
                />
              </label>
            ))}
          </section>
          {(summary || previous) && (
            <div class="source-preview-estimate">
              Candidate catalog only (task and reads excluded):{" "}
              {previous && summary && <>{formatEstimate(previous)} → </>}
              {summary ? (
                formatEstimate(summary)
              ) : (
                <>
                  {formatEstimate(previous!)} · {busy ? "Updating…" : "Estimate unavailable"}
                </>
              )}
            </div>
          )}
          {listing.partial && (
            <div class="source-preview-estimate">
              Source list is partial.{" "}
              <button type="button" disabled={busy} onClick={() => void refresh()}>
                {busy ? "Updating…" : "Refresh"}
              </button>
            </div>
          )}
        </>
      )}
    </div>
  );
}

function formatFileCount(count: number): string {
  return `${count.toLocaleString()} ${count === 1 ? "file" : "files"}`;
}

function formatEstimate(value: Preview): string {
  return `~${value.estimatedTokens.toLocaleString()} tokens · ${formatFileCount(value.eligibleCount)} eligible${value.partial ? " · Partial scan" : ""}`;
}
