import type { ComponentChildren } from "preact";

export interface ComposerModel {
  key: string;
  modelLabel: string;
  providerLabel: string;
}

export interface ModelPickerProps {
  models: readonly ComposerModel[];
  modelKey: string | null;
  modelLabel: string;
  onModelChange: (key: string) => void;
  loadingModels: boolean;
  refreshingModels: boolean;
  modelsError: string | null;
  onRetryModels: () => void;
  disabled: boolean;
}

export interface ComposerProps {
  text: string;
  onTextChange: (text: string) => void;
  models: readonly ComposerModel[];
  modelKey: string | null;
  modelLabel: string;
  onModelChange: (key: string) => void;
  hasProviders: boolean | null;
  loadingModels: boolean;
  refreshingModels: boolean;
  modelsError: string | null;
  onRetryModels: () => void;
  onSetUpProvider: () => void;
  reasoningEffort: string | null;
  reasoningOptions: readonly string[];
  onReasoningChange: (effort: string | null) => void;
  selectionLocked: boolean;
  canSend: boolean;
  canContinue: boolean;
  continuationAvailable: boolean;
  working: boolean;
  stopping: boolean;
  onSend: () => void;
  onContinue: () => void;
  onStop: () => void;
  indicators?: ComponentChildren;
}
