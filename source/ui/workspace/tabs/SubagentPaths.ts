import type { SubagentLink } from "../../chat/subagents/SubagentNavigation.ts";

export function subagentPaths(rootId: string, links: readonly SubagentLink[]): SubagentLink[][] {
  const paths: SubagentLink[][] = [];
  const visit = (parentId: string, path: SubagentLink[]) => {
    for (const link of links) {
      if (
        link.parentId !== parentId ||
        link.id === rootId ||
        path.some((item) => item.id === link.id)
      )
        continue;
      const next = [...path, link];
      paths.push(next);
      visit(link.id, next);
    }
  };
  visit(rootId, []);
  return paths;
}
