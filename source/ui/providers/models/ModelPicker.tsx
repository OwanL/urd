import type { TargetedKeyboardEvent } from "preact";
import { useEffect, useId, useMemo, useRef, useState } from "preact/hooks";
import { Icon } from "../../controls/Icon.tsx";
import { Popover, scrollOptionIntoView } from "../../controls/Popover.tsx";
import { Tooltip } from "../../controls/Tooltip.tsx";
import "../../controls/selector.css";
import "./model-picker.css";

export interface ModelPickerOption {
  key: string;
  modelId: string;
  modelLabel: string;
  providerLabel: string;
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
  const trigger = useRef<HTMLButtonElement>(null);
  const search = useRef<HTMLInputElement>(null);
  const activeOption = useRef<HTMLDivElement>(null);
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState("");
  const [activeKey, setActiveKey] = useState<string | null>(null);
  const expanded = open && !disabled;

  const term = query.trim().toLocaleLowerCase();
  const groups = useMemo(() => {
    if (!expanded) return [];
    const result = new Map<string, ModelPickerOption[]>();
    for (const model of models) {
      const searchable = `${model.modelLabel} ${model.modelId} ${model.providerLabel}`;
      if (!searchable.toLocaleLowerCase().includes(term)) continue;
      const group = result.get(model.providerLabel);
      if (group) group.push(model);
      else result.set(model.providerLabel, [model]);
    }
    return [...result];
  }, [expanded, models, term]);
  const options = useMemo(() => groups.flatMap(([, group]) => group), [groups]);
  const optionIndexes = useMemo(
    () => new Map(options.map((model, index) => [model, index])),
    [options],
  );
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

  function navigate(event: TargetedKeyboardEvent<HTMLInputElement>) {
    if (event.isComposing || event.keyCode === 229) return;
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
              role="combobox"
              list={undefined}
              aria-autocomplete="list"
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
          <div class="model-picker-options" id={`${id}-options`} role="listbox" aria-label="Models">
            {groups.map(([provider, group]) => (
              <div role="group" aria-label={provider} key={provider}>
                <div class="model-picker-provider" aria-hidden="true">
                  {provider}
                </div>
                {group.map((model) => {
                  const index = optionIndexes.get(model)!;
                  const active = index === activeIndex;
                  const visibleIdentity = `${model.modelLabel} ${model.providerLabel}`;
                  const showModelId =
                    term &&
                    !visibleIdentity.toLocaleLowerCase().includes(term) &&
                    model.modelId.toLocaleLowerCase().includes(term);
                  return (
                    <div
                      key={model.key}
                      id={`${id}-option-${index}`}
                      class={`model-picker-option${active ? " is-active" : ""}`}
                      role="option"
                      aria-selected={model.key === modelKey}
                      tabIndex={-1}
                      ref={active ? activeOption : null}
                      onPointerMove={() => setActiveKey(model.key)}
                      onPointerDown={(event) => event.preventDefault()}
                      onClick={() => choose(model.key)}
                    >
                      <span class="model-picker-option-identity">
                        <span class="model-picker-label">{model.modelLabel}</span>
                        {showModelId && <span class="model-picker-model-id">{model.modelId}</span>}
                      </span>
                      {model.key === modelKey && <Icon name="check" />}
                    </div>
                  );
                })}
              </div>
            ))}
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
