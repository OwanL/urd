import type { ToolDraft } from "./SessionEvents.ts";

export interface ToolDrafts {
  boot: string | null;
  items: readonly ToolDraft[];
  adopted?: Set<string>;
  request?: { session: string; executionId: string; requestId: string; watermark: number };
}

const emptyDrafts: readonly ToolDraft[] = [];

export class ToolDraftStore {
  private state: ToolDrafts = { boot: null, items: [] };
  private session: string | null = null;
  private snapshot: readonly ToolDraft[] = emptyDrafts;
  private listeners = new Map<string, Set<() => void>>();

  read(session: string): readonly ToolDraft[] {
    return session === this.session ? this.snapshot : emptyDrafts;
  }

  subscribe(session: string, listener: () => void): () => void {
    let listeners = this.listeners.get(session);
    if (!listeners) this.listeners.set(session, (listeners = new Set()));
    listeners.add(listener);
    return () => {
      listeners.delete(listener);
      if (!listeners.size) this.listeners.delete(session);
    };
  }

  watch(session: string | null): void {
    if (this.session === session) return;
    const previous = this.session;
    this.session = session;
    this.state = { boot: this.state.boot, items: [] };
    this.snapshot = emptyDrafts;
    if (previous) this.notify(previous);
    if (session) this.notify(session);
  }

  boot(boot: string): void {
    this.update(setToolDraftBoot(this.state, boot));
  }

  clear(): void {
    this.update({ boot: this.state.boot, items: [] });
  }

  disconnect(): void {
    this.update({ boot: null, items: [] });
  }

  status(_session: string, _status: string): void {
    // Session status does not prove that the history window contains a promoted entry.
  }

  apply(draft: ToolDraft): void {
    if (draft.session !== this.session) return;
    this.update(applyToolDraft(this.state, draft));
  }

  adopt(session: string, entryIds: readonly string[]): void {
    if (session === this.session) this.update(adoptToolDrafts(this.state, session, entryIds));
  }

  private update(state: ToolDrafts): void {
    if (state === this.state) return;
    this.state = state;
    const items = state.items.filter((draft) => draft.state !== "discarded");
    if (
      items.length === this.snapshot.length &&
      items.every((item, i) => item === this.snapshot[i])
    )
      return;
    this.snapshot = items.length ? items : emptyDrafts;
    if (this.session) this.notify(this.session);
  }

  private notify(session: string): void {
    for (const listener of this.listeners.get(session) ?? []) listener();
  }
}

function draftKey(draft: ToolDraft): string {
  return JSON.stringify([draft.session, draft.executionId, draft.requestId, draft.callId]);
}

export function adoptToolDrafts(
  previous: ToolDrafts,
  session: string,
  entryIds: readonly string[],
): ToolDrafts {
  const entries = new Set(entryIds);
  const matched = previous.items.filter(
    (draft) => draft.session === session && entries.has(draft.entryId),
  );
  if (!matched.length) return previous;
  const adopted = previous.adopted ?? new Set<string>();
  for (const draft of matched) {
    if (
      draft.session === previous.request?.session &&
      draft.executionId === previous.request.executionId &&
      draft.requestId === previous.request.requestId
    )
      adopted.add(draftKey(draft));
  }
  return {
    ...previous,
    adopted,
    items: previous.items.filter((draft) => !matched.includes(draft)),
  };
}

export function setToolDraftBoot(previous: ToolDrafts, boot: string): ToolDrafts {
  return previous.boot === boot ? previous : { boot, items: [] };
}

export function applyToolDraft(previous: ToolDrafts, draft: ToolDraft): ToolDrafts {
  if (draft.boot !== previous.boot) return previous;
  const request = previous.request;
  const sameRequest =
    request?.session === draft.session &&
    request.executionId === draft.executionId &&
    request.requestId === draft.requestId;
  if (!sameRequest) {
    // Request IDs are random; session event versions advance on persisted request starts.
    if (request && draft.version < request.watermark) return previous;
    previous = {
      ...previous,
      adopted: new Set<string>(),
      items: previous.items.filter((item) => item.state !== "discarded"),
      request: {
        session: draft.session,
        executionId: draft.executionId,
        requestId: draft.requestId,
        watermark: draft.version,
      },
    };
  } else if (draft.version > request.watermark) {
    previous = {
      ...previous,
      request: { ...request, watermark: draft.version },
    };
  }
  if (previous.adopted?.has(draftKey(draft))) return previous;
  const index = previous.items.findIndex(
    (item) =>
      item.session === draft.session &&
      item.executionId === draft.executionId &&
      item.requestId === draft.requestId &&
      item.callId === draft.callId,
  );
  const old = previous.items[index];
  if (old && draft.version < old.version) return previous;
  const next = [...previous.items];
  const value =
    old && draft.state === "promoted" && !draft.head && !draft.tail
      ? { ...draft, field: old.field ?? null, head: old.head, tail: old.tail }
      : draft;
  if (index < 0) next.push(value);
  else next[index] = value;
  const requests = new Map<string, ToolDraft[]>();
  for (const item of next) {
    const key = JSON.stringify([item.session, item.executionId, item.requestId]);
    const group = requests.get(key) ?? [];
    group.push(item);
    requests.set(key, group);
  }
  return {
    ...previous,
    items: [...requests.values()].flatMap((group) => group.sort((a, b) => a.order - b.order)),
  };
}
