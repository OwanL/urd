import type { SubagentLink } from "../chat/subagents/SubagentNavigation.ts";

export interface SubagentCall extends SubagentLink {
  readonly titlePending: boolean;
  readonly live: boolean;
  readonly modelId: string | null;
  readonly startedAt: string | null;
  readonly elapsedMilliseconds: number | null;
  readonly durationIncomplete: boolean;
  readonly cost: SubagentCost;
}

export interface SubagentCost {
  readonly own: SubagentCostAmount;
  readonly descendants: SubagentCostAmount;
  readonly total: SubagentCostAmount;
  readonly hasDescendants: boolean;
}

export interface SubagentCostAmount {
  readonly usd: number | null;
  readonly partial: boolean;
}
