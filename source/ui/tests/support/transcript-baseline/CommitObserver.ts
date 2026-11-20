import { DatabaseSync } from "node:sqlite";
import { isMainThread, parentPort, workerData } from "node:worker_threads";

const epoch = () => performance.timeOrigin + performance.now();

export type Commit = {
  negativeStart: number;
  positiveEnd: number;
  samples: number;
  maxQueryMs: number;
};

function observeCommit() {
  const { path, session, marker } = workerData as { path: string; session: string; marker: string };
  const db = new DatabaseSync(path, { readOnly: true });
  db.exec("PRAGMA query_only=ON; PRAGMA cache_size=-2048; PRAGMA busy_timeout=1000");
  const query =
    db.prepare(`SELECT o.text_utf8 FROM execution_output o JOIN execution_requests q USING(request_id)
    JOIN execution_runs r USING(run_id) WHERE r.session_id=? ORDER BY r.ordinal,q.request_index,o.chunk_index`);
  let negativeStart = epoch(),
    next = epoch(),
    samples = 0,
    maxQueryMs = 0;
  const deadline = epoch() + 30_000;
  parentPort!.postMessage({ ready: true });
  function sample() {
    const started = epoch();
    if (started > deadline) {
      db.close();
      throw new Error("No committed marker within 30 seconds");
    }
    if (started < next) {
      setImmediate(sample);
      return;
    }
    const rows = query.all(session);
    const found = rows
      .map((row) => Buffer.from(row.text_utf8 as Uint8Array).toString("utf8"))
      .join("")
      .includes(marker);
    const ended = epoch();
    samples++;
    maxQueryMs = Math.max(maxQueryMs, ended - started);
    if (found) {
      parentPort!.postMessage({
        negativeStart,
        positiveEnd: ended,
        samples,
        maxQueryMs,
      } satisfies Commit);
      db.close();
      return;
    }
    negativeStart = started;
    next = started + 2;
    setImmediate(sample);
  }
  sample();
}

if (!isMainThread) observeCommit();
