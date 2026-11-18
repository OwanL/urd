// null follows all connected providers; an explicit list (including []) never expands automatically.
export type SubagentProviderConnectionIds = readonly string[] | null;

export interface SubagentProviders {
  readonly connectionIds: SubagentProviderConnectionIds;
}

export function toggleSubagentProvider(
  selection: SubagentProviderConnectionIds,
  connectedIds: readonly string[],
  id: string,
  checked: boolean,
): SubagentProviderConnectionIds {
  const current = selection ?? connectedIds;
  if (!checked) return current.filter((member) => member !== id);
  return current.includes(id) ? selection : [...current, id];
}
