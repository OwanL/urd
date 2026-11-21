import { useState } from "preact/hooks";

type Kind = "instructions" | "skills";
type Source = { name: string; tokens?: number; children?: Source[] };

const sources: Record<Kind, Source> = {
  instructions: {
    name: "repos/urd",
    children: [
      { name: "AGENTS.md", tokens: 1800 },
      { name: "CLAUDE.md", tokens: 1100 },
      { name: "APPEND_SYSTEM.md", tokens: 800 },
      { name: "staged", children: [
        { name: "AGENTS.md", tokens: 1300 },
        { name: "GEMINI.md", tokens: 900 },
      ] },
      { name: "src", children: [
        { name: "AGENTS.md", tokens: 1600 },
        { name: "CLAUDE.md", tokens: 1200 },
        { name: "GEMINI.md", tokens: 800 },
        { name: "ui", children: [
          { name: "AGENTS.md", tokens: 1700 },
          { name: "CLAUDE.md", tokens: 1200 },
        ] },
      ] },
    ],
  },
  skills: {
    name: "repos/urd",
    children: [
      { name: "staged", children: [{ name: "skills", children: [{ name: "draft", children: [{ name: "SKILL.md", tokens: 220 }] }] }] },
      { name: ".agents", children: [
        { name: "skills", children: [
          { name: "code-review", children: [{ name: "SKILL.md", tokens: 320 }] },
          { name: "testing", children: [{ name: "SKILL.md", tokens: 230 }] },
          { name: "research", children: [
            { name: "web-research", children: [{ name: "SKILL.md", tokens: 260 }] },
            { name: "summarize", children: [{ name: "SKILL.md", tokens: 180 }] },
          ] },
        ] },
      ] },
      { name: ".claude", children: [
        { name: "skills", children: [
          { name: "documentation", children: [{ name: "SKILL.md", tokens: 210 }] },
        ] },
      ] },
    ],
  },
};

function leaves(node: Source): Source[] {
  return node.children ? node.children.flatMap(leaves) : [node];
}

function toggle(values: Set<string>, value: string): Set<string> {
  const next = new Set(values);
  if (next.has(value)) next.delete(value);
  else next.add(value);
  return next;
}

function estimate(tokens: number): string {
  return tokens >= 1000 ? `${(tokens / 1000).toFixed(1)}k` : String(tokens);
}

export function SourceIgnorePreview({ kind }: { kind: Kind }) {
  return <Preview key={kind} kind={kind} />;
}

function Preview({ kind }: { kind: Kind }) {
  const root = sources[kind];
  const [ignored, setIgnored] = useState<Set<string>>(() => new Set(["repos/urd/staged"]));
  const [filenames, setFilenames] = useState<Set<string>>(() => new Set(kind === "instructions" ? ["APPEND_SYSTEM.md"] : []));
  const [collapsed, setCollapsed] = useState<Set<string>>(() => new Set(kind === "instructions" ? ["repos/urd/src/ui"] : ["repos/urd/.claude", "repos/urd/.agents/skills/research"]));
  const all = leaves(root);
  const names = [...new Set(all.map((file) => file.name))];

  function eligible(node: Source, path: string, inherited = false): number {
    const excluded = inherited || ignored.has(path);
    if (!node.children) return excluded || filenames.has(node.name) ? 0 : node.tokens ?? 0;
    return node.children.reduce((sum, child) => sum + eligible(child, `${path}/${child.name}`, excluded), 0);
  }

  function eligibleCount(node: Source, path: string, inherited = false): number {
    const excluded = inherited || ignored.has(path);
    if (!node.children) return excluded || filenames.has(node.name) ? 0 : 1;
    return node.children.reduce((sum, child) => sum + eligibleCount(child, `${path}/${child.name}`, excluded), 0);
  }

  function row(node: Source, path: string, depth: number, inherited: boolean) {
    const byName = !node.children && filenames.has(node.name);
    const locked = inherited || byName;
    const checked = locked || ignored.has(path);
    const count = leaves(node).length;
    return (
      <div key={path}>
        <div className="source-preview-row" style={{ paddingLeft: 8 + depth * 18 }}>
          {node.children ? (
            <button className="source-preview-disclosure" type="button" aria-label={`${collapsed.has(path) ? "Expand" : "Collapse"} ${path}`} aria-expanded={!collapsed.has(path)} onClick={() => setCollapsed((current) => toggle(current, path))}>
              {collapsed.has(path) ? "▸" : "▾"}
            </button>
          ) : <span className="source-preview-spacer" />}
          <span className="source-preview-name" title={path}>{node.name}</span>
          {node.children && <span className="source-preview-muted">{count} {count === 1 ? "file" : "files"}</span>}
          {locked && <span className="source-preview-inherited">{inherited ? "Inherited" : "Filename"}</span>}
          <input type="checkbox" aria-label={`Ignore ${path}`} checked={checked} disabled={locked} onChange={() => setIgnored((current) => toggle(current, path))} />
        </div>
        {node.children && !collapsed.has(path) && node.children.map((child) => row(child, `${path}/${child.name}`, depth + 1, checked))}
      </div>
    );
  }

  return (
    <div className="setup-content source-preview">
      <style>{`
        .source-preview { font: inherit; color: var(--text-primary); }
        .source-preview-section { margin: 0 0 16px; }
        .source-preview-heading { display: flex; align-items: baseline; justify-content: space-between; gap: 12px; margin: 0 0 8px; font-size: 13px; font-weight: 600; }
        .source-preview-muted, .source-preview-inherited { color: var(--text-secondary); font-size: 12px; font-weight: 400; }
        .source-preview-names { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 8px 16px; }
        .source-preview-name-option { display: flex; align-items: center; gap: 8px; min-width: 0; font-size: 13px; }
        .source-preview-name-option .source-preview-muted { margin-left: auto; white-space: nowrap; }
        .source-preview-tree { border: 1px solid var(--border-subtle); border-radius: 6px; padding: 4px 0; }
        .source-preview-row { display: flex; align-items: center; gap: 7px; min-height: 30px; padding-right: 10px; font-size: 13px; }
        .source-preview-disclosure, .source-preview-spacer { flex: none; width: 20px; height: 24px; }
        .source-preview-disclosure { padding: 0; border: 0; background: transparent; color: var(--text-secondary); cursor: pointer; font: inherit; }
        .source-preview-disclosure:focus-visible { outline: 2px solid var(--text-primary); outline-offset: -2px; }
        .source-preview-name { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
        .source-preview-row > input { flex: none; margin: 0 0 0 auto; }
        .source-preview-inherited { margin-left: auto; white-space: nowrap; }
        .source-preview-inherited + input { margin-left: 0; }
        .source-preview input { accent-color: var(--text-primary); }
        .source-preview-estimate { color: var(--text-secondary); font-size: 12px; }
        @media (max-width: 420px) { .source-preview-names { grid-template-columns: 1fr; } }
      `}</style>
      {kind === "instructions" && (
        <section className="source-preview-section" aria-label="Ignore by filename">
          <div className="source-preview-heading">Ignore by filename <span className="source-preview-muted">All folders</span></div>
          <div className="source-preview-names">
            {names.map((name) => {
              const count = all.filter((file) => file.name === name).length;
              return <label className="source-preview-name-option" key={name}>
                <input type="checkbox" checked={filenames.has(name)} onChange={() => setFilenames((current) => toggle(current, name))} />
                <span>{name}</span><span className="source-preview-muted">{count} {count === 1 ? "file" : "files"}</span>
              </label>;
            })}
          </div>
        </section>
      )}
      <section className="source-preview-section" aria-label="Ignore by location">
        <div className="source-preview-heading">Ignore by location <span className="source-preview-muted">Ignore</span></div>
        <div className="source-preview-tree">{row(root, root.name, 0, false)}</div>
      </section>
      <div className="source-preview-estimate">
        Estimated pruner input (illustrative): ~{estimate(all.reduce((sum, file) => sum + (file.tokens ?? 0), 0))} → ~{estimate(eligible(root, root.name))} tokens · {eligibleCount(root, root.name)} of {all.length} files eligible
      </div>
    </div>
  );
}
