import type { ModelPickerOption } from "./ModelPicker.tsx";

export type ModelSortKey = "model" | "provider" | "input" | "output" | "images";

export interface ModelSort {
  readonly key: ModelSortKey;
  readonly descending: boolean;
}

export function sortModelPickerOptions(
  models: readonly ModelPickerOption[],
  favorites: ReadonlySet<string>,
  sort: ModelSort,
): ModelPickerOption[] {
  return [...models].sort((left, right) => {
    const favoriteOrder = Number(favorites.has(right.key)) - Number(favorites.has(left.key));
    if (favoriteOrder) return favoriteOrder;

    const missingPriceOrder = compareMissingPrice(left, right, sort.key);
    if (missingPriceOrder) return missingPriceOrder;
    const order = compareBy(left, right, sort.key);
    return (sort.descending ? -order : order) || compareIdentity(left, right);
  });
}

function compareBy(left: ModelPickerOption, right: ModelPickerOption, key: ModelSortKey): number {
  if (key === "input") return comparePrice(left.inputPrice, right.inputPrice);
  if (key === "output") return comparePrice(left.outputPrice, right.outputPrice);
  if (key === "images") return Number(left.supportsImages) - Number(right.supportsImages);
  if (key === "provider") return left.providerLabel.localeCompare(right.providerLabel);
  return left.modelLabel.localeCompare(right.modelLabel);
}

function comparePrice(left: number | null, right: number | null): number {
  return left === null || right === null ? 0 : left - right;
}

function compareMissingPrice(
  left: ModelPickerOption,
  right: ModelPickerOption,
  key: ModelSortKey,
): number {
  if (key !== "input" && key !== "output") return 0;
  const leftPrice = key === "input" ? left.inputPrice : left.outputPrice;
  const rightPrice = key === "input" ? right.inputPrice : right.outputPrice;
  const leftMissing = leftPrice === null || !Number.isFinite(leftPrice);
  const rightMissing = rightPrice === null || !Number.isFinite(rightPrice);
  return leftMissing === rightMissing ? 0 : leftMissing ? 1 : -1;
}

function compareIdentity(left: ModelPickerOption, right: ModelPickerOption): number {
  return (
    left.modelLabel.localeCompare(right.modelLabel) ||
    left.providerLabel.localeCompare(right.providerLabel) ||
    left.modelId.localeCompare(right.modelId) ||
    left.connectionId.localeCompare(right.connectionId)
  );
}
