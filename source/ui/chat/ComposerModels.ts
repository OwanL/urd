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
  onRefreshModels?: (() => void) | undefined;
  refreshingModels?: boolean | undefined;
  modelsError?: string | null | undefined;
  disabled: boolean;
}

export interface ComposerProps {
  text: string;
  onTextChange: (text: string) => void;
  models: readonly ComposerModel[];
  modelKey: string | null;
  modelLabel: string;
  onModelChange: (key: string) => void;
  onRefreshModels?: () => void;
  refreshingModels?: boolean;
  modelsError?: string | null;
  reasoningEffort: string | null;
  reasoningOptions: readonly string[];
  onReasoningChange: (effort: string | null) => void;
  selectionLocked: boolean;
  canSend: boolean;
  working: boolean;
  stopping: boolean;
  onSend: () => void;
  onStop: () => void;
}
