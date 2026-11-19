// Response-header and preparation facts from the session contract. History entries and
// snapshots carry slim summaries; full candidate/result/preview detail is read on demand.

// The boundary of the first exchanged message, before any compaction boundary exists.
export const initialBoundaryId = "00000000-0000-0000-0000-000000000000";

export interface ResponseHeaderInfo {
  readonly modelId: string;
  readonly reasoningEffort: string;
  readonly startedAt: string | null;
}

export interface PrepassResource {
  readonly id: string;
  readonly label: string;
  readonly path?: string | null;
}

export interface PrepassSummary {
  readonly boundaryId: string;
  readonly category: string;
  readonly state: string;
  readonly candidateCount: number | null;
  readonly resultCount: number | null;
  readonly error?: string | null;
  // True when no prepass model was configured so the chat's own model did the work.
  readonly usedSessionModel?: boolean;
}

export interface PendingResponse {
  readonly header: ResponseHeaderInfo;
  readonly preparation: readonly PrepassSummary[];
}

export interface PrepassDetailsQuery {
  readonly boundaryId: string;
  readonly category: string;
  readonly historyRevision: number;
  readonly candidateOffset: number;
  readonly resultOffset: number;
}

// One bounded detail page; candidates and results continue through paging offsets.
export interface PrepassDetails {
  readonly executionId: string;
  readonly boundaryId: string;
  readonly category: string;
  readonly state: string;
  readonly modelId: string | null;
  readonly reasoningEffort: string | null;
  readonly startedAt: string;
  readonly finishedAt: string | null;
  readonly costUsd: number | null;
  readonly costIsEstimated: boolean;
  readonly candidateCount: number;
  readonly resultCount: number | null;
  readonly candidates: readonly PrepassResource[];
  readonly results: readonly PrepassResource[];
  readonly candidateOffset: number;
  readonly resultOffset: number;
  readonly reasoning: string;
  readonly output: string;
  readonly reasoningTruncated: boolean;
  readonly outputTruncated: boolean;
  readonly title?: string | null;
  readonly error?: string | null;
}
