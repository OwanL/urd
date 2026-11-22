export interface SourceNode {
  name: string;
  path: string;
  children: SourceNode[];
  count: number;
  file: boolean;
}

// Filename rules match independently of filesystem path casing.
export function filenameKey(name: string): string {
  return name.replaceAll("-", "_").toLowerCase();
}

// Compare Windows paths without changing the spelling sent back to the server.
export function pathKey(path: string): string {
  const slashes = path.replaceAll("\\", "/");
  const normalized = slashes.replace(/\/+$/, "") || (slashes.startsWith("/") ? "/" : "");
  return /^[a-z]:/i.test(normalized) || normalized.startsWith("//")
    ? normalized.toLowerCase()
    : normalized;
}

export function isWithin(path: string, parent: string): boolean {
  const childKey = pathKey(path);
  const parentKey = pathKey(parent);
  return (
    childKey === parentKey ||
    childKey.startsWith(parentKey.endsWith("/") ? parentKey : `${parentKey}/`)
  );
}

export type Selection = "none" | "partial" | "all";

export interface LocationRules {
  paths: string[];
  includes: string[];
}

function locationIgnored(path: string, rules: LocationRules): boolean {
  let longest = -1;
  let ignored = false;
  for (const [entries, value] of [
    [rules.paths, true],
    [rules.includes, false],
  ] as const) {
    for (const entry of entries) {
      const length = pathKey(entry).length;
      if (length >= longest && isWithin(path, entry)) {
        longest = length;
        ignored = value;
      }
    }
  }
  return ignored;
}

// Filename rules remain independent of location exceptions.
export function sourceSelection(
  node: SourceNode,
  rules: LocationRules,
  filenames: string[],
): Selection {
  const ignored = locationIgnored(node.path, rules);
  if (node.file)
    return ignored || filenames.some((name) => filenameKey(name) === filenameKey(node.name))
      ? "all"
      : "none";
  const exceptions = [...rules.paths, ...rules.includes].some(
    (path) =>
      pathKey(path) !== pathKey(node.path) &&
      isWithin(path, node.path) &&
      locationIgnored(path, rules) !== ignored,
  );
  if (exceptions) return "partial";
  if (ignored) return "all";
  const children = node.children.map((child) => sourceSelection(child, rules, filenames));
  if (children.length && children.every((child) => child === "all")) return "all";
  if (children.some((child) => child !== "none")) return "partial";
  return "none";
}

export function setSourceIgnored(
  rules: LocationRules,
  node: SourceNode,
  ignored: boolean,
): LocationRules {
  const remaining = {
    paths: rules.paths.filter((path) => !isWithin(path, node.path)),
    includes: rules.includes.filter((path) => !isWithin(path, node.path)),
  };
  if (locationIgnored(node.path, remaining) !== ignored)
    (ignored ? remaining.paths : remaining.includes).push(node.path);
  return remaining;
}

export function buildSourceTree(paths: string[], workingDirectory: string): SourceNode[] {
  const roots: SourceNode[] = [];
  const nodes = new Map<string, SourceNode>();
  const rootKey = pathKey(workingDirectory);
  for (const path of paths) {
    const normalized = path.replaceAll("\\", "/");
    const inside = isWithin(path, workingDirectory);
    const relative = inside
      ? normalized
          .slice(workingDirectory.replaceAll("\\", "/").replace(/\/+$/, "").length)
          .replace(/^\/+/, "")
      : normalized.replace(/^\/+/, "");
    const parts = relative.split("/").filter(Boolean);
    const prefix = inside
      ? workingDirectory.replace(/[\\/]+$/, "") || (workingDirectory.startsWith("/") ? "/" : "")
      : normalized.startsWith("//")
        ? "//"
        : normalized.startsWith("/")
          ? "/"
          : "";
    let current = prefix;
    let siblings = roots;
    if (inside) {
      let root = nodes.get(rootKey);
      if (!root) {
        root = {
          name:
            workingDirectory
              .replace(/[\\/]+$/, "")
              .split(/[\\/]/)
              .pop() || workingDirectory,
          path: workingDirectory,
          children: [],
          count: 0,
          file: false,
        };
        nodes.set(rootKey, root);
        roots.push(root);
      }
      root.count++;
      siblings = root.children;
    }
    for (let index = 0; index < parts.length; index++) {
      const part = parts[index]!;
      current =
        current === "/" || current === "//"
          ? `${current}${part}`
          : current
            ? `${current.replace(/[\\/]+$/, "")}/${part}`
            : part;
      const key = pathKey(current);
      let node = nodes.get(key);
      if (!node) {
        node = {
          name: part,
          path: current,
          children: [],
          count: 0,
          file: index === parts.length - 1,
        };
        nodes.set(key, node);
        siblings.push(node);
      }
      node.count++;
      siblings = node.children;
    }
  }
  return roots;
}
