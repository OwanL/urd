export interface UsageTotals {
  costUsd: number;
  requests: number;
  estimatedRequests: number;
  unknownRequests: number;
  activeRequests: number;
}

export interface UsageBreakdown {
  provider: string;
  model: string;
  purpose: string;
  totals: UsageTotals;
  isOther?: boolean;
}

export interface UsagePoint {
  at: string;
  totals: UsageTotals;
  breakdown: UsageBreakdown[];
}

export interface UsageTokens {
  inputTokens: number | null;
  outputTokens: number | null;
  cachedInputTokens: number | null;
  cacheWrite30mTokens: number | null;
  cacheWrite12hTokens: number | null;
  reasoningTokens?: number | null;
  totalTokens?: number | null;
}

export interface UsageRates {
  model: string;
  source: string;
  capturedAt: string;
  input: number;
  output: number;
  cachedInput: number | null;
  cachedOutput?: number | null;
  cacheWrite30m?: number | null;
  cacheWrite12h?: number | null;
  longContextThreshold?: number | null;
  longInput?: number | null;
  longOutput?: number | null;
  longCachedInput?: number | null;
}

export interface UsageRequest {
  id: string;
  sessionId: string | null;
  provider: string;
  model: string;
  purpose: string;
  startedAt: string;
  finishedAt: string | null;
  costUsd: number | null;
  isEstimate: boolean;
  problem: string | null;
  tokens: UsageTokens | null;
  rates: UsageRates | null;
}

export interface UsageSummary {
  totals: UsageTotals;
  breakdown: UsageBreakdown[];
  history: UsagePoint[];
  recent: UsageRequest[];
}

export type UsageRange = "day" | "sevenDays";

export interface SessionUsageQuery {
  sessionId: string;
  detail?: false;
}

export interface GlobalUsageQuery {
  from: string;
  to: string;
  detail?: false;
}

export type UsageQuery = SessionUsageQuery | GlobalUsageQuery;
