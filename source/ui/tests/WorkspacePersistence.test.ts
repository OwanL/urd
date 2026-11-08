import assert from "node:assert/strict";
import test from "node:test";
import { WorkspacePersistence } from "../app/WorkspacePersistence.ts";
import { beginSubmission, editDraft, emptyWorkspace, newChat } from "../app/WorkspaceState.ts";
import type { ChatState } from "../app/WorkspaceState.ts";

class MemoryStorage implements Storage {
  readonly values = new Map<string, string>();
  writes: string[] = [];

  get length(): number {
    return this.values.size;
  }

  clear(): void {
    this.values.clear();
  }

  getItem(key: string): string | null {
    return this.values.get(key) ?? null;
  }

  key(index: number): string | null {
    return [...this.values.keys()][index] ?? null;
  }

  removeItem(key: string): void {
    this.values.delete(key);
  }

  setItem(key: string, value: string): void {
    this.writes.push(key);
    this.values.set(key, String(value));
  }
}

const key = "urd.workspace.v1";
const sessionId = "10000000-0000-0000-0000-000000000001";

function chat(draft = ""): ChatState {
  return draft ? editDraft(newChat(null), draft) : newChat(null);
}

function persistence(storage: Storage): WorkspacePersistence {
  return new WorkspacePersistence(() => storage, key);
}

void test("ordinary draft writes update one draft record without serializing the workspace", () => {
  const storage = new MemoryStorage();
  const saved = persistence(storage);
  const state = emptyWorkspace();
  state.tabs = [sessionId];
  state.activeId = sessionId;
  state.chats[sessionId] = newChat(null);
  saved.save(state);
  storage.writes.length = 0;

  saved.saveDraft(sessionId, chat("new draft"));

  assert.deepEqual(storage.writes, [`${key}.draft.${sessionId}`]);
  assert.equal(storage.getItem(key), JSON.stringify(state));
  assert.deepEqual(saved.load().state.chats[sessionId]?.draft, "new draft");
});

void test("a synchronous workspace journal wins ties with a stale draft overlay", () => {
  const storage = new MemoryStorage();
  const saved = persistence(storage);
  const state = emptyWorkspace();
  state.tabs = [sessionId];
  state.activeId = sessionId;
  state.chats[sessionId] = chat("submitted");
  saved.saveDraft(sessionId, state.chats[sessionId]!);
  saved.save(state);

  const original = {
    submissionId: "30000000-0000-0000-0000-000000000003",
    expectedHistoryRevision: 0,
    text: "submitted",
    llmSelection: null,
  };
  const journal = {
    ...state,
    chats: { ...state.chats, [sessionId]: beginSubmission(chat("submitted"), original) },
  };
  saved.save(journal);

  const restored = saved.load().state.chats[sessionId]!;
  assert.equal(restored.draft, "submitted");
  assert.equal(restored.pending?.status, "uncertain");
  assert.equal(restored.pending?.original.text, "submitted");
});

void test("draft overlays apply only when newer than the durable workspace snapshot", () => {
  const storage = new MemoryStorage();
  const saved = persistence(storage);
  const state = emptyWorkspace();
  state.tabs = [sessionId];
  state.activeId = sessionId;
  state.chats[sessionId] = newChat(null);
  saved.save(state);
  saved.saveDraft(sessionId, chat("newer"));

  assert.equal(saved.load().state.chats[sessionId]?.draft, "newer");

  const later = { ...state, chats: { ...state.chats, [sessionId]: chat("cleared") } };
  saved.save(later);
  assert.equal(saved.load().state.chats[sessionId]?.draft, "cleared");
});

void test("corrupt workspace or draft data stays unreadable instead of resetting saved state", () => {
  const storage = new MemoryStorage();
  storage.values.set(key, "{");
  assert.match(persistence(storage).load().error ?? "", /could not be read/);
  assert.equal(storage.getItem(key), "{");

  const valid = emptyWorkspace();
  valid.tabs = [sessionId];
  valid.activeId = sessionId;
  valid.chats[sessionId] = newChat(null);
  storage.values.set(key, JSON.stringify(valid));
  storage.values.set(`${key}.draft.${sessionId}`, "{");
  assert.match(persistence(storage).load().error ?? "", /could not be read/);
  assert.equal(storage.getItem(key), JSON.stringify(valid));
});

void test("workspace writes validate and propagate storage failures", () => {
  const storage = new MemoryStorage();
  const saved = persistence(storage);
  const invalid = emptyWorkspace();
  invalid.version = 2 as 1;
  assert.throws(() => saved.save(invalid), /Invalid workspace/);

  storage.setItem = () => {
    throw new Error("quota");
  };
  const failing = persistence(storage);
  assert.throws(() => failing.save(emptyWorkspace()), /quota/);
});
