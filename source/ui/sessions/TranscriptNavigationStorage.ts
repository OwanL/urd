import { isTextEntry, type TranscriptEntryRef } from "./Messages.ts";
import type { TranscriptSourceCursor } from "./TranscriptViewport.ts";

export interface TranscriptNavigationState {
  readonly historyRevision: number;
  readonly positionVersion: number;
  readonly followsLatest: boolean;
  readonly anchor: {
    readonly entry: TranscriptEntryRef;
    readonly sourceCursor: TranscriptSourceCursor | null;
    readonly viewportOffset: number;
  } | null;
  readonly width: number | null;
  readonly profileVersion: string | null;
}

export interface TranscriptNavigationStorageLike {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
  removeItem(key: string): void;
}

export interface NavigationStorageResult<T> {
  readonly value: T;
  readonly error: string | null;
}

export interface TranscriptNavigationExpectation {
  readonly width?: number | null;
  readonly profileVersion?: string | null;
}

const schemaVersion = 1;
const maximumKeyLength = 256;
const maximumRecordLength = 16_384;
const keyPrefix = "urd.transcript-navigation.v1.";

export class TranscriptNavigationStorage {
  private readonly storage: TranscriptNavigationStorageLike;

  constructor(storage: TranscriptNavigationStorageLike) {
    this.storage = storage;
  }

  load(
    sessionId: string,
    expected: TranscriptNavigationExpectation = {},
  ): NavigationStorageResult<{ state: TranscriptNavigationState | null; pixelReuse: boolean }> {
    const key = storageKey(sessionId);
    if (key === null)
      return { value: { state: null, pixelReuse: false }, error: "Invalid session identifier." };
    let serialized: string | null;
    try {
      serialized = this.storage.getItem(key);
    } catch (error) {
      return { value: { state: null, pixelReuse: false }, error: errorMessage(error) };
    }
    if (serialized === null) return { value: { state: null, pixelReuse: false }, error: null };
    let state: TranscriptNavigationState | null = null;
    try {
      if (serialized.length <= maximumRecordLength) {
        const record: unknown = JSON.parse(serialized);
        if (isRecord(record) && record.version === schemaVersion && isState(record.state))
          state = record.state;
      }
    } catch {
      // Malformed persisted state is intentionally discarded rather than migrated.
    }
    if (state === null) {
      try {
        this.storage.removeItem(key);
        return { value: { state: null, pixelReuse: false }, error: null };
      } catch (error) {
        return { value: { state: null, pixelReuse: false }, error: errorMessage(error) };
      }
    }
    const pixelReuse =
      (expected.width === undefined || expected.width === state.width) &&
      (expected.profileVersion === undefined || expected.profileVersion === state.profileVersion);
    return { value: { state, pixelReuse }, error: null };
  }

  save(sessionId: string, state: TranscriptNavigationState): NavigationStorageResult<void> {
    const key = storageKey(sessionId);
    if (key === null || !isState(state))
      return { value: undefined, error: "Invalid transcript navigation state." };
    try {
      const serialized = JSON.stringify({ version: schemaVersion, state: projectState(state) });
      if (serialized.length > maximumRecordLength)
        return { value: undefined, error: "Transcript navigation record is too large." };
      this.storage.setItem(key, serialized);
      return { value: undefined, error: null };
    } catch (error) {
      return { value: undefined, error: errorMessage(error) };
    }
  }

  clear(sessionId: string): NavigationStorageResult<void> {
    const key = storageKey(sessionId);
    if (key === null) return { value: undefined, error: "Invalid session identifier." };
    try {
      this.storage.removeItem(key);
      return { value: undefined, error: null };
    } catch (error) {
      return { value: undefined, error: errorMessage(error) };
    }
  }
}

export type NavigationScheduler = (callback: () => void) => unknown;
export type NavigationCancellation = (handle: unknown) => void;

// Call schedule after each state change; flush on departure/page hide and cancel when the owner is disposed.
export class DebouncedTranscriptNavigationWriter {
  private pending: unknown | null = null;
  private latest: { sessionId: string; state: TranscriptNavigationState } | null = null;
  private readonly storage: TranscriptNavigationStorage;
  private readonly schedule: NavigationScheduler;
  private readonly cancelScheduled: NavigationCancellation;

  constructor(
    storage: TranscriptNavigationStorage,
    schedule: NavigationScheduler,
    cancelScheduled: NavigationCancellation,
  ) {
    this.storage = storage;
    this.schedule = schedule;
    this.cancelScheduled = cancelScheduled;
  }

  scheduleWrite(sessionId: string, state: TranscriptNavigationState): void {
    this.latest = { sessionId, state };
    if (this.pending !== null) this.cancelScheduled(this.pending);
    this.pending = this.schedule(() => {
      this.pending = null;
      this.flush();
    });
  }

  flush(): NavigationStorageResult<void> {
    if (this.pending !== null) this.cancelScheduled(this.pending);
    this.pending = null;
    const latest = this.latest;
    this.latest = null;
    return latest === null
      ? { value: undefined, error: null }
      : this.storage.save(latest.sessionId, latest.state);
  }

  cancel(): void {
    if (this.pending !== null) this.cancelScheduled(this.pending);
    this.pending = null;
    this.latest = null;
  }
}

function projectState(state: TranscriptNavigationState): TranscriptNavigationState {
  const anchor = state.anchor;
  return {
    historyRevision: state.historyRevision,
    positionVersion: state.positionVersion,
    followsLatest: state.followsLatest,
    anchor:
      anchor === null
        ? null
        : {
            entry: {
              executionId: anchor.entry.executionId,
              entryId: anchor.entry.entryId,
              kind: anchor.entry.kind,
              historyRevision: anchor.entry.historyRevision,
            },
            sourceCursor:
              anchor.sourceCursor === null
                ? null
                : {
                    segmentIndex: anchor.sourceCursor.segmentIndex,
                    utf8Offset: anchor.sourceCursor.utf8Offset,
                  },
            viewportOffset: anchor.viewportOffset,
          },
    width: state.width,
    profileVersion: state.profileVersion,
  };
}

function storageKey(sessionId: string): string | null {
  if (typeof sessionId !== "string" || sessionId.length === 0 || sessionId.length > 128)
    return null;
  const key = `${keyPrefix}${encodeURIComponent(sessionId)}`;
  return key.length <= maximumKeyLength ? key : null;
}

function isState(value: unknown): value is TranscriptNavigationState {
  if (
    !isRecord(value) ||
    !validCounter(value.historyRevision) ||
    !validCounter(value.positionVersion) ||
    typeof value.followsLatest !== "boolean"
  )
    return false;
  if (
    value.width !== null &&
    (typeof value.width !== "number" || !Number.isFinite(value.width) || value.width <= 0)
  )
    return false;
  if (
    value.profileVersion !== null &&
    (typeof value.profileVersion !== "string" || value.profileVersion.length > 128)
  )
    return false;
  if (value.anchor === null) return true;
  if (
    !isRecord(value.anchor) ||
    !isEntry(value.anchor.entry) ||
    !validNumber(value.anchor.viewportOffset)
  )
    return false;
  if (value.anchor.entry.historyRevision !== value.historyRevision) return false;
  const cursor = value.anchor.sourceCursor;
  if (cursor !== null && !isTextEntry(value.anchor.entry)) return false;
  return (
    cursor === null ||
    (isRecord(cursor) && validCounter(cursor.segmentIndex) && validCounter(cursor.utf8Offset))
  );
}

function isEntry(value: unknown): value is TranscriptEntryRef {
  return (
    isRecord(value) &&
    typeof value.executionId === "string" &&
    value.executionId.length > 0 &&
    value.executionId.length <= 512 &&
    typeof value.entryId === "string" &&
    value.entryId.length > 0 &&
    value.entryId.length <= 512 &&
    (value.kind === "user" ||
      value.kind === "text" ||
      value.kind === "tool" ||
      value.kind === "toolCompletion") &&
    validCounter(value.historyRevision)
  );
}
function validNumber(value: unknown): value is number {
  return typeof value === "number" && Number.isFinite(value);
}
function validCounter(value: unknown): value is number {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0;
}
function isRecord(value: unknown): value is Record<string, any> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
