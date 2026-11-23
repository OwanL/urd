import { requestJson } from "../http/Requests.ts";
import type { SubagentProviderConnectionIds } from "../providers/subagents/SubagentProviderSelection.ts";

export type AuxiliaryModelPurpose = "tools" | "skills" | "instructions" | "title";
export type ProtectedResourceCategory = "tools" | "skills" | "instructions";
export type ModelBucket = "small" | "medium" | "large";

export interface ConfiguredModel {
  readonly connectionId: string;
  readonly modelId: string;
}

export interface ModelChoice {
  readonly bucket: ModelBucket | null;
  readonly connectionId: string | null;
  readonly modelId: string | null;
}

export interface AuxiliaryModelPreferences {
  readonly tools: ModelChoice;
  readonly skills: ModelChoice;
  readonly instructions: ModelChoice;
  readonly title: ModelChoice;
  readonly small: readonly ConfiguredModel[];
  readonly medium: readonly ConfiguredModel[];
  readonly large: readonly ConfiguredModel[];
}

export interface ProtectedResourcePreferences {
  readonly tools: readonly string[];
  readonly skills: readonly string[];
  readonly instructions: readonly string[];
}

export interface SelectableResource {
  readonly id: string;
  readonly name: string;
}

export interface SelectableResources {
  readonly tools: readonly SelectableResource[];
  readonly skills: readonly SelectableResource[];
  readonly instructions: readonly SelectableResource[];
}

export interface Preferences {
  readonly workingDirectory: string | null;
  readonly compactionTriggerTokenCount: number | null;
  readonly modelChoices: AuxiliaryModelPreferences;
  readonly protectedResources: ProtectedResourcePreferences;
  readonly subagentProviderConnectionIds: SubagentProviderConnectionIds;
  readonly maxSubagentDepth: number;
}

// Typed access to browser preferences; construction performs no I/O.
export class PreferencesClient {
  private readonly origin: string;

  constructor(origin: URL) {
    this.origin = origin.origin;
  }

  get(signal?: AbortSignal): Promise<Preferences> {
    return requestJson(new URL("/api/preferences", this.origin), "GET", undefined, signal);
  }

  getSelectableResources(signal?: AbortSignal): Promise<SelectableResources> {
    return requestJson(
      new URL("/api/preferences/resources", this.origin),
      "GET",
      undefined,
      signal,
    );
  }

  setWorkingDirectory(workingDirectory: string | null): Promise<Preferences> {
    return requestJson(new URL("/api/preferences/working-directory", this.origin), "POST", {
      workingDirectory,
    });
  }

  setCompactionTriggerTokenCount(compactionTriggerTokenCount: number | null): Promise<Preferences> {
    return requestJson(new URL("/api/preferences/compaction-trigger", this.origin), "POST", {
      compactionTriggerTokenCount,
    });
  }

  setModelChoice(purpose: AuxiliaryModelPurpose, choice: ModelChoice): Promise<Preferences> {
    return requestJson(new URL("/api/preferences/model-choice", this.origin), "POST", {
      purpose,
      choice,
    });
  }

  setBucketModels(bucket: ModelBucket, models: readonly ConfiguredModel[]): Promise<Preferences> {
    return requestJson(new URL("/api/preferences/model-bucket", this.origin), "POST", {
      bucket,
      models,
    });
  }

  setSubagentProviders(connectionIds: SubagentProviderConnectionIds): Promise<Preferences> {
    return requestJson(new URL("/api/preferences/subagent-providers", this.origin), "POST", {
      connectionIds,
    });
  }

  setMaxSubagentDepth(maxSubagentDepth: number): Promise<Preferences> {
    return requestJson(new URL("/api/preferences/subagent-depth", this.origin), "POST", {
      maxSubagentDepth,
    });
  }

  setProtectedItems(
    category: ProtectedResourceCategory,
    items: readonly string[],
  ): Promise<Preferences> {
    return requestJson(new URL("/api/preferences/protected-resources", this.origin), "POST", {
      category,
      items,
    });
  }
}
