export interface FolderBreadcrumb {
  name: string;
  path: string;
}

export function folderBreadcrumbs(path: string): readonly FolderBreadcrumb[] {
  let separator: string;
  let rootName: string;
  let rootPath: string;
  let remainder: string;

  if (path.startsWith("\\\\")) {
    separator = "\\";
    const segments = path.slice(2).split(/\\+/).filter(Boolean);
    rootName = `\\\\${segments.shift() ?? ""}\\${segments.shift() ?? ""}`;
    rootPath = `${rootName}\\`;
    remainder = segments.join(separator);
  } else if (/^[A-Za-z]:\\/.test(path)) {
    separator = "\\";
    rootName = path.slice(0, 3);
    rootPath = rootName;
    remainder = path.slice(3);
  } else if (path.startsWith("/")) {
    separator = "/";
    rootName = "/";
    rootPath = rootName;
    remainder = path;
  } else return [];

  const breadcrumbs: FolderBreadcrumb[] = [{ name: rootName, path: rootPath }];
  let currentPath = rootPath;
  for (const name of remainder.split(separator).filter(Boolean)) {
    currentPath = `${currentPath}${currentPath.endsWith(separator) ? "" : separator}${name}`;
    breadcrumbs.push({ name, path: currentPath });
  }
  return breadcrumbs;
}
