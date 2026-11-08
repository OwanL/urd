import type { TargetedKeyboardEvent } from "preact";
import { useEffect, useId, useLayoutEffect, useMemo, useRef, useState } from "preact/hooks";
import type { ComposerModel, ModelPickerProps } from "./ComposerModels.ts";

export function ModelPicker({
  models,
  modelKey,
  modelLabel,
  onModelChange,
  onRefreshModels,
  refreshingModels = false,
  modelsError,
  disabled,
}: ModelPickerProps) {
  const id = useId();
  const root = useRef<HTMLDivElement>(null);
  const trigger = useRef<HTMLButtonElement>(null);
  const search = useRef<HTMLInputElement>(null);
  const activeOption = useRef<HTMLDivElement>(null);
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState("");
  const [activeKey, setActiveKey] = useState<string | null>(null);
  const expanded = open && !disabled;

  const groups = useMemo(() => {
    const term = query.trim().toLocaleLowerCase();
    const result = new Map<string, ComposerModel[]>();
    for (const model of models) {
      if (!`${model.modelLabel} ${model.providerLabel}`.toLocaleLowerCase().includes(term))
        continue;
      const group = result.get(model.providerLabel);
      if (group) group.push(model);
      else result.set(model.providerLabel, [model]);
    }
    return [...result];
  }, [models, query]);
  const options = useMemo(() => groups.flatMap(([, group]) => group), [groups]);
  const optionIndexes = useMemo(
    () => new Map(options.map((model, index) => [model, index])),
    [options],
  );
  const activeIndex = Math.max(
    0,
    options.findIndex((model) => model.key === activeKey),
  );
  const activeModel = options[activeIndex];

  function close(returnFocus: boolean) {
    setOpen(false);
    if (returnFocus) trigger.current?.focus();
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
    if (event.key !== "ArrowDown" && event.key !== "ArrowUp") return;
    event.preventDefault();
    if (!options.length) return;
    const direction = event.key === "ArrowDown" ? 1 : -1;
    const next = (activeIndex + direction + options.length) % options.length;
    setActiveKey(options[next]!.key);
  }

  useLayoutEffect(() => {
    if (expanded) search.current?.focus();
  }, [expanded]);

  useEffect(() => {
    if (disabled) setOpen(false);
  }, [disabled]);

  useEffect(() => {
    if (!expanded) return;
    function dismiss(event: PointerEvent) {
      if (!root.current?.contains(event.target as Node)) setOpen(false);
    }
    document.addEventListener("pointerdown", dismiss);
    return () => document.removeEventListener("pointerdown", dismiss);
  }, [expanded]);

  useEffect(() => {
    if (expanded) activeOption.current?.scrollIntoView({ block: "nearest" });
  }, [expanded, activeModel?.key]);

  return (
    <div
      class="composer-model"
      ref={root}
      onKeyDown={(event) => {
        if (event.key === "Escape" && expanded) {
          event.preventDefault();
          event.stopPropagation();
          close(true);
        }
      }}
      onFocusOut={(event) => {
        if (!event.currentTarget.contains(event.relatedTarget as Node | null)) setOpen(false);
      }}
    >
      <button
        class="composer-selector composer-model-trigger"
        type="button"
        ref={trigger}
        disabled={disabled}
        aria-label={`Model: ${modelLabel}`}
        aria-haspopup="dialog"
        aria-expanded={expanded}
        aria-controls={expanded ? `${id}-popover` : undefined}
        title={modelLabel}
        onClick={() => (expanded ? close(false) : show())}
        onKeyDown={(event) => {
          if (event.key === "ArrowDown" || event.key === "ArrowUp") {
            event.preventDefault();
            show();
          }
        }}
      >
        <span class="composer-model-label">{modelLabel}</span>
        <span class="composer-chevron" aria-hidden="true">
          ⌄
        </span>
      </button>
      {expanded && (
        <div
          class="composer-model-popover"
          id={`${id}-popover`}
          role="dialog"
          aria-label="Choose model"
        >
          <input
            class="composer-model-search"
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
          <div
            class="composer-model-options"
            id={`${id}-options`}
            role="listbox"
            aria-label="Models"
          >
            {groups.map(([provider, group]) => (
              <div role="group" aria-label={provider} key={provider}>
                <div class="composer-model-provider" aria-hidden="true">
                  {provider}
                </div>
                {group.map((model) => {
                  const index = optionIndexes.get(model)!;
                  const active = index === activeIndex;
                  return (
                    <div
                      key={model.key}
                      id={`${id}-option-${index}`}
                      class={`composer-model-option${active ? " is-active" : ""}`}
                      role="option"
                      aria-selected={model.key === modelKey}
                      tabIndex={-1}
                      ref={active ? activeOption : null}
                      title={model.modelLabel}
                      onPointerDown={(event) => event.preventDefault()}
                      onClick={() => choose(model.key)}
                    >
                      <span class="composer-model-label">{model.modelLabel}</span>
                      {model.key === modelKey && <span aria-hidden="true">✓</span>}
                    </div>
                  );
                })}
              </div>
            ))}
          </div>
          {!options.length && (
            <div class="composer-model-empty" role="status">
              No models found
            </div>
          )}
          {(onRefreshModels || modelsError) && (
            <div class="composer-model-footer">
              {modelsError && (
                <div class="composer-model-error" role="status">
                  {modelsError}
                </div>
              )}
              {onRefreshModels && (
                <button
                  class="composer-model-refresh"
                  type="button"
                  aria-label="Refresh models"
                  aria-disabled={refreshingModels}
                  aria-busy={refreshingModels}
                  onClick={() => {
                    if (!refreshingModels) onRefreshModels();
                  }}
                >
                  {refreshingModels ? "Refreshing…" : "Refresh models"}
                </button>
              )}
            </div>
          )}
        </div>
      )}
    </div>
  );
}
