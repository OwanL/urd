export function formatTokenCount(tokens: number | null | undefined): string {
  if (tokens === null || tokens === undefined || !Number.isFinite(tokens) || tokens < 0) return "?";
  if (tokens < 1_000) return String(Math.floor(tokens));
  return `${Number((tokens / 1_000).toFixed(1))}k`;
}
