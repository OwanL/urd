export function parseSubagentDepthInput(value: string): number | null {
  if (!/^\d+$/.test(value)) return null;
  const depth = Number(value);
  return Number.isSafeInteger(depth) && depth <= 2_147_483_647 ? depth : null;
}
