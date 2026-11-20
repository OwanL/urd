import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { readFile } from "node:fs/promises";
import { DatabaseSync } from "node:sqlite";

const groups = 20_000;

export function seed(path: string, ids: string[], selection: string) {
  const db = new DatabaseSync(path);
  try {
    assert.equal(
      db.prepare("PRAGMA user_version").get()?.user_version,
      16,
      "Transcript fixture schema changed; requalify its SQL before updating this guard.",
    );
    db.exec("PRAGMA foreign_keys=ON; PRAGMA cache_size=-8192; PRAGMA synchronous=NORMAL;");
    const input = db.prepare(`INSERT INTO input_decisions
      (session_id,input_id,expected_history_revision,original_text_utf8,decision,run_id,llm_selection_json) VALUES (?,?,0,?,1,?,?)`);
    const run = db.prepare(`INSERT INTO execution_runs
      (session_id,run_id,history_revision,input_id,outcome) VALUES (?,?,0,?,0)`);
    const request = db.prepare(`INSERT INTO execution_requests
      (request_id,run_id,request_index,response_id,next_chunk_index,response_final,outcome,attempt)
      VALUES (?,?,0,?,?,1,0,2)`);
    const assigned = db.prepare(`INSERT INTO execution_inputs
      (session_id,input_id,run_id,request_id,outcome) VALUES (?,?,?,?,0)`);
    const message = db.prepare(`INSERT INTO execution_messages
      (session_id,history_revision,run_id,role,message_id,input_id,request_id,first_chunk_index,end_chunk_index,call_id,tool_name)
      VALUES (?,0,?,?,?,?,?,0,?,?,?)`);
    const output = db.prepare("INSERT INTO execution_output VALUES (?,?,?,?)");
    const tool = db.prepare(`INSERT INTO execution_tools
      (run_id,request_id,call_index,call_id,name,arguments_json,started,outcome,result_json)
      VALUES (?,?,?,?, 'fixture_tool','{}',1,0,?)`);
    const userBody = Buffer.from("u".repeat(6000));
    const ordinaryReply = Buffer.from("r".repeat(6250));
    const largeReply = Buffer.from("R".repeat(5_000_000));
    const ordinaryResult = JSON.stringify("t".repeat(1998));
    const largeResult = JSON.stringify("T".repeat(4_999_998));
    const fixtures = [];
    for (const [index, session] of ids.entries()) {
      console.error(`Seeding heavy session ${index + 1}/5 (100,000 entries)`);
      // Commit bounded batches; no corpus-sized JS arrays or provider-context reconstruction.
      for (let group = 0; group < groups; group++) {
        if (group % 1000 === 0) db.exec("BEGIN");
        const runId = randomUUID(),
          inputId = randomUUID(),
          requestId = randomUUID(),
          replyId = randomUUID();
        const body =
          group === 0
            ? largeReply
            : group === groups - 1
              ? Buffer.from(`baseline-heavy-${index}-latest ${"r".repeat(6226)}`)
              : ordinaryReply;
        const segments = Math.ceil(body.byteLength / 16_384);
        input.run(session, inputId, userBody, runId, selection);
        run.run(session, runId, inputId);
        request.run(requestId, runId, replyId, segments);
        assigned.run(session, inputId, runId, requestId);
        message.run(session, runId, 0, inputId, inputId, requestId, null, null, null);
        message.run(session, runId, 1, replyId, null, requestId, segments, null, null);
        for (let segment = 0; segment < segments; segment++)
          output.run(
            requestId,
            segment,
            body.subarray(segment * 16_384, (segment + 1) * 16_384),
            Number(segment === segments - 1),
          );
        for (let call = 0; call < 3; call++) {
          const callId = `call-${requestId}-${call}`;
          tool.run(
            runId,
            requestId,
            call,
            callId,
            group === 0 && call === 0 ? largeResult : ordinaryResult,
          );
          message.run(
            session,
            runId,
            2,
            randomUUID(),
            null,
            requestId,
            null,
            callId,
            "fixture_tool",
          );
        }
        if (group % 1000 === 999) db.exec("COMMIT");
      }
      const facts = db
        .prepare(`SELECT
        (SELECT count(*) FROM execution_messages WHERE session_id=?) entries,
        (SELECT count(*) FROM execution_tools t JOIN execution_runs r USING(run_id) WHERE r.session_id=?) tools,
        (SELECT sum(length(original_text_utf8)) FROM input_decisions WHERE session_id=?) +
        (SELECT sum(length(o.text_utf8)) FROM execution_output o JOIN execution_requests q USING(request_id)
          JOIN execution_runs r USING(run_id) WHERE r.session_id=?) transcriptContentBytes,
        (SELECT sum(length(CAST(arguments_json AS BLOB))+length(CAST(result_json AS BLOB)))
          FROM execution_tools t JOIN execution_runs r USING(run_id) WHERE r.session_id=?) toolPayloadBytes`)
        .get(session, session, session, session, session);
      assert.equal(facts?.entries, 100_000);
      assert.equal(facts?.tools, 60_000);
      assert.equal(facts?.transcriptContentBytes, 249_993_750);
      assert.equal(facts?.toolPayloadBytes, 125_118_000);
      fixtures.push({
        session,
        ...facts,
        totalStoredPayloadBytes:
          Number(facts?.transcriptContentBytes) + Number(facts?.toolPayloadBytes),
        singleReplyBytes: largeReply.byteLength,
        singleToolResultBytes: Buffer.byteLength(largeResult),
        maxOutputSegmentBytes: 16_384,
        externalImageReferences: 1000,
      });
    }
    assert.equal(db.prepare("PRAGMA foreign_key_check").get(), undefined);
    db.exec("PRAGMA wal_checkpoint(TRUNCATE)");
    return fixtures;
  } finally {
    db.close();
  }
}

export async function queryPlans(path: string, session: string) {
  // Read the actual production constants rather than silently benchmarking a stale copy.
  const source = await readFile(
    new URL(
      "../../../../backend/harness/storage/transcript/TranscriptHistoryReader.cs",
      import.meta.url,
    ),
    "utf8",
  );
  const window = source
    .match(/WindowSql\(bool ascending\) => \$\$"""([\s\S]*?)"""/)?.[1]
    ?.replaceAll('{{(ascending ? ">=" : "<=")}}', "<=")
    .replaceAll('{{(ascending ? "ASC" : "DESC")}}', "DESC");
  const lookahead = source.match(/LookaheadSql = """([\s\S]*?)"""/)?.[1];
  assert.ok(
    window && lookahead,
    "Production query format changed; update the measurement extractor",
  );
  const db = new DatabaseSync(path, { readOnly: true });
  try {
    db.exec("PRAGMA query_only=ON; PRAGMA cache_size=-8192");
    const parameters = {
      $session: session,
      $revision: 0,
      $boundary: 9_223_372_036_854_775_807n,
      $limit: 60,
    };
    const statements = [
      {
        name: "revision",
        sql: "SELECT history_revision FROM sessions WHERE session_id=$session",
        parameters: { $session: session },
      },
      { name: "latestWindow", sql: window, parameters },
      {
        name: "lookahead",
        sql: lookahead,
        parameters: { $session: session, $revision: 0, $first: 99_941, $last: 100_000 },
      },
    ];
    return statements.map(({ name, sql, parameters }) => {
      const plan = db.prepare(`EXPLAIN QUERY PLAN ${sql}`).all(parameters);
      const statement = db.prepare(sql);
      const times = [];
      let rows = 0;
      for (let sample = 0; sample < 10; sample++) {
        const start = performance.now();
        rows = statement.all(parameters).length;
        times.push(performance.now() - start);
      }
      return {
        name,
        sql,
        plan,
        rows,
        milliseconds: times,
        caveat: "Node SQLite diagnostic, not backend instrumentation; OS file cache not flushed",
      };
    });
  } finally {
    db.close();
  }
}
