import type { TargetedKeyboardEvent } from "preact";
import { useContext, useEffect, useId, useMemo, useRef, useState } from "preact/hooks";
import { Icon } from "../../controls/Icon.tsx";
import { Popover, scrollOptionIntoView } from "../../controls/Popover.tsx";
import { Tooltip } from "../../controls/Tooltip.tsx";
import { ModelFavoritesContext } from "./ModelFavorites.tsx";
import { sortModelPickerOptions, type ModelSort, type ModelSortKey } from "./ModelPickerOrder.ts";
import "../../controls/selector.css";
import "./model-picker.css";

export interface ModelPickerOption {
  key: string;
  connectionId: string;
  modelId: string;
  modelLabel: string;
  providerLabel: string;
  inputPrice: number | null;
  outputPrice: number | null;
  priceSource: string | null;
  priceCapturedAt: string | null;
  supportsImages: boolean;
}

interface ModelPickerProps {
  models: readonly ModelPickerOption[];
  modelKey: string | null;
  modelLabel: string;
  ariaLabel?: string;
  onModelChange: (key: string) => void;
  loadingModels?: boolean;
  refreshingModels?: boolean;
  modelsError?: string | null;
  onRetryModels?: () => void;
  disabled: boolean;
}

const columns: readonly [ModelSortKey, string][] = [
  ["model", "Model"],
  ["provider", "Provider"],
  ["input", "In"],
  ["output", "Out"],
  ["images", "Img"],
];

export function ModelPicker({
  models,
  modelKey,
  modelLabel,
  ariaLabel,
  onModelChange,
  loadingModels,
  refreshingModels,
  modelsError,
  onRetryModels,
  disabled,
}: ModelPickerProps) {
  const id = useId();
  const favorites = useContext(ModelFavoritesContext);
  const trigger = useRef<HTMLButtonElement>(null);
  const search = useRef<HTMLInputElement>(null);
  const activeOption = useRef<HTMLDivElement>(null);
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState("");
  const [activeKey, setActiveKey] = useState<string | null>(null);
  const [sort, setSort] = useState<ModelSort>({ key: "model", descending: false });
  const expanded = open && !disabled;

  const term = query.trim().toLocaleLowerCase();
  const orderedModels = useMemo(
    () => sortModelPickerOptions(models, favorites.keys, sort),
    [favorites.keys, models, sort],
  );
  const options = useMemo(() => {
    if (!expanded) return [];
    return orderedModels.filter((model) => {
      const searchable = `${model.modelLabel} ${model.modelId} ${model.providerLabel}`;
      return searchable.toLocaleLowerCase().includes(term);
    });
  }, [expanded, orderedModels, term]);
  const keyedActiveIndex = options.findIndex((model) => model.key === activeKey);
  const activeIndex = keyedActiveIndex >= 0 ? keyedActiveIndex : term && options.length ? 0 : -1;
  const activeModel = activeIndex >= 0 ? options[activeIndex] : undefined;
  const selectedModel = models.find((model) => model.key === modelKey);
  const sameLabelModels = selectedModel
    ? models.filter(
        (model) => model.key !== selectedModel.key && model.modelLabel === selectedModel.modelLabel,
      )
    : [];
  const selectionQualifier =
    selectedModel && sameLabelModels.length
      ? sameLabelModels.some((model) => model.providerLabel === selectedModel.providerLabel)
        ? selectedModel.modelId
        : selectedModel.providerLabel
      : null;
  const triggerLabel = selectionQualifier ? `${modelLabel} · ${selectionQualifier}` : modelLabel;

  function close(returnFocus: boolean) {
    setOpen(false);
    if (returnFocus) trigger.current?.focus({ preventScroll: true });
  }

  function show() {
    if (disabled) return;
    setQuery("");
    setActiveKey(modelKey);
    setOpen(true);
  }

  function choose(key: string) {
    if (disabled) return;
    close(true);
    onModelChange(key);
  }

  function toggleFavorite(model: ModelPickerOption | undefined) {
    if (!model || favorites.disabled) return;
    favorites.toggle({ connectionId: model.connectionId, modelId: model.modelId });
  }

  function navigate(event: TargetedKeyboardEvent<HTMLInputElement>) {
    if (event.isComposing || event.keyCode === 229) return;
    if (event.key === "ContextMenu" || (event.key === "F10" && event.shiftKey)) {
      event.preventDefault();
      event.stopPropagation();
      toggleFavorite(activeModel);
      return;
    }
    if (event.key === "Enter") {
      event.preventDefault();
      if (activeModel) choose(activeModel.key);
      return;
    }
    if (event.key === "Home" || event.key === "End") {
      event.preventDefault();
      const next = event.key === "Home" ? options[0] : options.at(-1);
      if (next) setActiveKey(next.key);
      return;
    }
    if (event.key !== "ArrowDown" && event.key !== "ArrowUp") return;
    event.preventDefault();
    if (!options.length) return;
    const direction = event.key === "ArrowDown" ? 1 : -1;
    const next =
      activeIndex < 0
        ? direction > 0
          ? 0
          : options.length - 1
        : (activeIndex + direction + options.length) % options.length;
    setActiveKey(options[next]!.key);
  }

  function changeSort(key: ModelSortKey) {
    setSort((current) => ({
      key,
      descending: current.key === key ? !current.descending : false,
    }));
  }

  useEffect(() => {
    if (disabled) setOpen(false);
  }, [disabled]);

  useEffect(() => {
    if (expanded) scrollOptionIntoView(activeOption.current);
  }, [expanded, activeModel?.key]);

  return (
    <div class="model-picker">
      <Tooltip
        content={
          selectedModel
            ? `${selectedModel.providerLabel} · ${selectedModel.modelLabel}`
            : modelLabel
        }
        disabled={expanded}
      >
        <button
          class="selector model-picker-trigger"
          type="button"
          ref={trigger}
          disabled={disabled}
          aria-label={ariaLabel ?? `Model: ${triggerLabel}`}
          aria-haspopup="dialog"
          aria-expanded={expanded}
          aria-controls={expanded ? `${id}-popover` : undefined}
          onClick={() => (expanded ? close(false) : show())}
          onKeyDown={(event) => {
            if (event.key === "ArrowDown" || event.key === "ArrowUp") {
              event.preventDefault();
              show();
            }
          }}
        >
          <span class="model-picker-label">{triggerLabel}</span>
          <Icon name="chevron" />
        </button>
      </Tooltip>
      {expanded && (
        <Popover
          anchor={trigger}
          initialFocus={search}
          onClose={() => setOpen(false)}
          placement="above"
          class="model-picker-popover"
          id={`${id}-popover`}
          label="Choose model"
        >
          <div class="model-picker-search-row">
            <Icon name="search" />
            <input
              class="model-picker-search"
              ref={search}
              type="search"
              placeholder="Search models"
              aria-label="Search models"
              aria-description="Right-click a model to toggle favorite. Use Shift+F10 or the Context Menu key for the highlighted model."
              role="combobox"
              list={undefined}
              aria-autocomplete="list"
              autoComplete="off"
              spellcheck={false}
              aria-expanded="true"
              aria-controls={`${id}-options`}
              aria-activedescendant={activeModel ? `${id}-option-${activeIndex}` : undefined}
              value={query}
              onInput={(event) => {
                setQuery(event.currentTarget.value);
                setActiveKey(null);
              }}
              onKeyDown={navigate}
            />
          </div>
          <div class="model-picker-header">
            {columns.map(([key, label]) => (
              <button
                key={key}
                type="button"
                onClick={() => {
                  changeSort(key);
                  search.current?.focus({ preventScroll: true });
                }}
                aria-label={`Sort by ${label}`}
                aria-description={
                  sort.key === key
                    ? `Sorted ${sort.descending ? "descending" : "ascending"}`
                    : "Not sorted by this column"
                }
                aria-pressed={sort.key === key}
              >
                {label}
                <span aria-hidden="true">
                  {sort.key === key ? (sort.descending ? " ↓" : " ↑") : ""}
                </span>
              </button>
            ))}
          </div>
          <div class="model-picker-options" id={`${id}-options`} role="listbox" aria-label="Models">
            {options.map((model, index) => {
              const active = index === activeIndex;
              const favorite = favorites.keys.has(model.key);
              const visibleIdentity = `${model.modelLabel} ${model.providerLabel}`;
              const showModelId =
                term &&
                !visibleIdentity.toLocaleLowerCase().includes(term) &&
                model.modelId.toLocaleLowerCase().includes(term);
              const inputPrice = formatPrice(model.inputPrice);
              const outputPrice = formatPrice(model.outputPrice);
              return (
                <div
                  key={model.key}
                  id={`${id}-option-${index}`}
                  class={`model-picker-option${active ? " is-active" : ""}`}
                  role="option"
                  aria-selected={model.key === modelKey}
                  aria-label={`${model.modelLabel}, ${model.providerLabel}/${model.modelId}, input price ${inputPrice ?? "unavailable"} per million tokens, output price ${outputPrice ?? "unavailable"} per million tokens, ${model.supportsImages ? "supports images" : "text only"}${favorite ? ", favorited" : ""}`}
                  title={modelTitle(model, favorite)}
                  tabIndex={-1}
                  ref={active ? activeOption : null}
                  onPointerMove={() => setActiveKey(model.key)}
                  onPointerDown={(event) => event.preventDefault()}
                  onContextMenu={(event) => {
                    event.preventDefault();
                    event.stopPropagation();
                    toggleFavorite(model);
                  }}
                  onClick={() => choose(model.key)}
                >
                  <span class="model-picker-column model-picker-model">
                    <span class="model-picker-selection" aria-hidden="true">
                      {model.key === modelKey ? "✓" : ""}
                    </span>
                    <span class="model-picker-option-identity">
                      <span class="model-picker-label">{model.modelLabel}</span>
                      {showModelId && <span class="model-picker-model-id">{model.modelId}</span>}
                    </span>
                    {favorite && (
                      <span class="model-picker-favorite" aria-hidden="true">
                        ★
                      </span>
                    )}
                  </span>
                  <span class="model-picker-column model-picker-provider">
                    {model.providerLabel}
                  </span>
                  <span class="model-picker-column model-picker-price">{inputPrice ?? "—"}</span>
                  <span class="model-picker-column model-picker-price">{outputPrice ?? "—"}</span>
                  <span
                    class="model-picker-column model-picker-images"
                    aria-label={model.supportsImages ? "Supports images" : "Text only"}
                  >
                    {model.supportsImages ? "✓" : "—"}
                  </span>
                </div>
              );
            })}
          </div>
          {!options.length && (!modelsError || query.trim()) && (
            <div class="model-picker-empty">
              <span role="status">
                {query.trim()
                  ? "No models match your search"
                  : loadingModels || refreshingModels
                    ? "Loading models…"
                    : "No models available"}
              </span>
            </div>
          )}
          {modelsError && (
            <div class="model-picker-footer">
              <div class="model-picker-error" role="status">
                {modelsError}
              </div>
              {onRetryModels && (
                <button
                  class="model-picker-retry"
                  type="button"
                  aria-label="Retry loading models"
                  disabled={refreshingModels}
                  aria-busy={refreshingModels}
                  onClick={onRetryModels}
                >
                  <Icon name="refresh" />
                  {refreshingModels ? "Retrying…" : "Retry"}
                </button>
              )}
            </div>
          )}
        </Popover>
      )}
    </div>
  );
}

function formatPrice(price: number | null): string | null {
  return price === null || !Number.isFinite(price) ? null : `$${price.toFixed(2)}`;
}

function modelTitle(model: ModelPickerOption, favorite: boolean): string {
  const prices =
    model.inputPrice === null && model.outputPrice === null
      ? "Reference API pricing unavailable"
      : `Reference API pricing: ${formatPrice(model.inputPrice) ?? "—"}/M in, ${formatPrice(model.outputPrice) ?? "—"}/M out`;
  const source = model.priceSource
    ? `Source: ${model.priceSource}${model.priceCapturedAt ? ` (${model.priceCapturedAt.slice(0, 10)})` : ""}`
    : null;
  return [
    model.modelLabel,
    `${model.providerLabel}/${model.modelId}`,
    prices,
    source,
    favorite ? "Right-click to remove favorite" : "Right-click to favorite",
  ]
    .filter(Boolean)
    .join("\n");
}
