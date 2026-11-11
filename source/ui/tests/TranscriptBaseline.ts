// Run from source/ui: node tests/TranscriptBaseline.ts
// Build Vite, then the Release server first. Not part of any test suite.
import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { randomUUID } from "node:crypto";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { cpus, freemem, hostname, platform, release, tmpdir, totalmem } from "node:os";
import { join } from "node:path";
import { DatabaseSync } from "node:sqlite";
import { promisify } from "node:util";
import { isMainThread, parentPort, Worker, workerData } from "node:worker_threads";
import { chromium, type Page } from "playwright";
import { encodeWorkspace, newChat } from "../app/WorkspaceState.ts";
import { SessionClient } from "../sessions/SessionClient.ts";
import { startLocalModelServer } from "./LocalModelServer.ts";
import { ServerProcess, ServerProcessNotStoppedError } from "./ServerProcess.ts";
const runFile = promisify(execFile);
const pause = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));
const epoch = () => performance.timeOrigin + performance.now();
const groups = 20_000;
const idleMs = 16_000;

type Task = { at: number; duration: number };
type Request = { at: number; path: string; method: string };
type Probe = {
  timers: number;
  heartbeats: number[];
  events: { name: string; at: number; session?: string; status?: string }[];
  tasks: Task[];
  longTasksSupported: boolean;
};
type Memory = { workingSetBytes: number; processes: number; method: string };
type Clock = { offsetMs: number; uncertaintyMs: number; roundTripMs: number };
class ProcessExitUnconfirmed extends Error {}

type Commit = { negativeStart: number; positiveEnd: number; samples: number; maxQueryMs: number };

async function startServer(root: string) {
  let server: ServerProcess;
  try {
    server = await ServerProcess.start({ dataRoot: root });
  } catch (error) {
    if (error instanceof ServerProcessNotStoppedError)
      throw new ProcessExitUnconfirmed(error.message);
    throw error;
  }
  return {
    origin: server.origin,
    pid: server.pid,
    async stop() {
      try {
        await server.stop();
      } catch (error) {
        if (error instanceof ServerProcessNotStoppedError)
          throw new ProcessExitUnconfirmed(error.message);
        throw error;
      }
    },
  };
}

function seed(path: string, ids: string[], selection: string) {
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

async function queryPlans(path: string, session: string) {
  // Read the actual production constants rather than silently benchmarking a stale copy.
  const source = await readFile(
    new URL("../../backend/harness/storage/TranscriptHistoryReader.cs", import.meta.url),
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

async function processMemory(pid: number): Promise<Memory> {
  if (process.platform === "win32") {
    const script = `$all=@(Get-CimInstance Win32_Process); $ids=[System.Collections.Generic.HashSet[int]]::new(); [void]$ids.Add(${pid}); do { $added=$false; foreach($p in $all) { if($ids.Contains([int]$p.ParentProcessId) -and $ids.Add([int]$p.ProcessId)) { $added=$true } } } while($added); $sum=0L; $count=0; foreach($id in $ids) { $p=Get-Process -Id $id -ErrorAction SilentlyContinue; if($p) { $sum+=$p.WorkingSet64; $count++ } }; @{workingSetBytes=$sum;processes=$count;method='Windows WorkingSet64 sum; shared pages counted per process'} | ConvertTo-Json -Compress`;
    const result = await runFile(
      "powershell.exe",
      ["-NoProfile", "-NonInteractive", "-Command", script],
      { windowsHide: true },
    );
    return JSON.parse(result.stdout) as Memory;
  }
  const { stdout } = await runFile("ps", ["-eo", "pid=,ppid=,rss="]);
  const rows = stdout
    .trim()
    .split("\n")
    .map((line) => line.trim().split(/\s+/).map(Number));
  const ids = new Set([pid]);
  let size = 0;
  while (size !== ids.size) {
    size = ids.size;
    for (const [id, parent] of rows) if (id && parent && ids.has(parent)) ids.add(id);
  }
  return {
    workingSetBytes: rows.reduce(
      (sum, [id, , rss]) => sum + (id && ids.has(id) ? (rss ?? 0) * 1024 : 0),
      0,
    ),
    processes: rows.filter(([id]) => id && ids.has(id)).length,
    method: "ps RSS sum; shared pages counted per process",
  };
}

async function installProbe(page: Page) {
  await page.addInitScript(() => {
    const probe: Probe = {
      timers: 0,
      heartbeats: [],
      events: [],
      tasks: [],
      longTasksSupported: PerformanceObserver.supportedEntryTypes.includes("longtask"),
    };
    (window as unknown as { __baseline: Probe }).__baseline = probe;
    if (probe.longTasksSupported)
      new PerformanceObserver((list) => {
        for (const entry of list.getEntries()) {
          if (probe.tasks.length < 20_000)
            probe.tasks.push({
              at: performance.timeOrigin + entry.startTime,
              duration: entry.duration,
            });
        }
      }).observe({ type: "longtask", buffered: true });
    const timeout = window.setTimeout.bind(window);
    const interval = window.setInterval.bind(window);
    window.setTimeout = ((handler: TimerHandler, ms?: number, ...args: unknown[]) =>
      timeout(
        typeof handler === "function"
          ? () => {
              probe.timers++;
              handler(...args);
            }
          : handler,
        ms,
      )) as typeof window.setTimeout;
    window.setInterval = ((handler: TimerHandler, ms?: number, ...args: unknown[]) =>
      interval(
        typeof handler === "function"
          ? () => {
              probe.timers++;
              handler(...args);
            }
          : handler,
        ms,
      )) as typeof window.setInterval;
    const original = window.fetch.bind(window);
    window.fetch = async (input, init) => {
      const response = await original(input, init);
      const url = new URL(
        typeof input === "string" ? input : input instanceof URL ? input : input.url,
        location.href,
      );
      if (url.pathname !== "/api/events" || (init?.method ?? "GET") !== "GET") return response;
      void (async () => {
        const reader = response.clone().body!.getReader();
        const decoder = new TextDecoder();
        let buffer = "";
        while (true) {
          const next = await reader.read();
          if (next.done) return;
          buffer += decoder.decode(next.value, { stream: true });
          let boundary: number;
          while ((boundary = buffer.indexOf("\n\n")) >= 0) {
            const frame = buffer.slice(0, boundary);
            buffer = buffer.slice(boundary + 2);
            const at = performance.timeOrigin + performance.now();
            if (frame.startsWith(": heartbeat")) probe.heartbeats.push(at);
            const name = frame.match(/^event: (.+)$/m)?.[1];
            const data = frame.match(/^data: (.+)$/m)?.[1];
            if (name && data && probe.events.length < 20_000) {
              const value = JSON.parse(data) as { session?: string };
              probe.events.push({ name, at, ...value });
            }
          }
        }
      })().catch(() => {});
      return response;
    };
  });
}

async function calibrateClock(page: Page): Promise<Clock> {
  const samples = [];
  for (let sample = 0; sample < 20; sample++) {
    const before = epoch();
    const browserAt = await page.evaluate(() => performance.timeOrigin + performance.now());
    const after = epoch();
    samples.push({
      offsetMs: (before + after) / 2 - browserAt,
      // Include the browser's 0.1 ms timestamp precision at both bounds.
      uncertaintyMs: (after - before) / 2 + 0.1,
      roundTripMs: after - before,
    });
  }
  return samples.reduce((best, sample) => (sample.roundTripMs < best.roundTripMs ? sample : best));
}

async function probe(page: Page, clock: Clock) {
  const value = await page.evaluate(() => (window as unknown as { __baseline: Probe }).__baseline);
  return {
    ...value,
    heartbeats: value.heartbeats.map((at) => at + clock.offsetMs),
    events: value.events.map((event) => ({ ...event, at: event.at + clock.offsetMs })),
    tasks: value.tasks.map((task) => ({ ...task, at: task.at + clock.offsetMs })),
  };
}

async function markerTime(page: Page, marker: string, clock: Clock) {
  const at = await page.evaluate(
    (marker) =>
      new Promise<number>((resolve, reject) => {
        const deadline = setTimeout(() => {
          observer.disconnect();
          reject(new Error(`Missing DOM marker: ${marker}`));
        }, 30_000);
        function check() {
          if (
            ![...document.querySelectorAll("#chat-view .transcript .message-text")].some((node) =>
              node.textContent?.includes(marker),
            )
          )
            return;
          clearTimeout(deadline);
          observer.disconnect();
          // DOM mutation followed by a frame opportunity; not a compositor/pixel timestamp.
          requestAnimationFrame(() => resolve(performance.timeOrigin + performance.now()));
        }
        const observer = new MutationObserver(check);
        observer.observe(document.body, { childList: true, characterData: true, subtree: true });
        check();
      }),
    marker,
  );
  return at + clock.offsetMs;
}

function summarizeTasks(tasks: Task[], start: number, end: number) {
  const values = tasks
    .filter((task) => task.at >= start && task.at < end)
    .map((task) => task.duration);
  return {
    count: values.length,
    totalMs: values.reduce((sum, value) => sum + value, 0),
    maxMs: Math.max(0, ...values),
  };
}

function summarizeGETs(requests: Request[], start: number, end: number) {
  const reads = requests.filter((r) => r.method === "GET" && r.at >= start && r.at < end);
  const seconds = (end - start) / 1000;
  return {
    durationMs: end - start,
    total: reads.length,
    perSecond: reads.length / seconds,
    byKind: Object.fromEntries(
      ["snapshot", "history", "content"].map((kind) => {
        const count = reads.filter((r) => r.path.endsWith(`/${kind}`)).length;
        return [kind, { count, perSecond: count / seconds }];
      }),
    ),
  };
}

async function main() {
  // No user-supplied data root: every write belongs to this newly allocated temp directory.
  const root = await mkdtemp(join(tmpdir(), "urd-transcript-baseline-"));
  const data = join(root, "data");
  const browserTemp = join(root, "browser");
  const report: Record<string, unknown> = {
    scope:
      "Stage 1 only: one page, five heavy Urd tabs plus sixth streaming session; latest 60 metadata rows with lazy bounded bodies. Cold means first session mount; warm means return/remount, NOT retained virtualized history or cold disk.",
    machine: {
      hostname: hostname(),
      platform: platform(),
      release: release(),
      cpu: cpus()[0]?.model,
      logicalCpus: cpus().length,
      totalMemoryBytes: totalmem(),
      freeMemoryBytes: freemem(),
      node: process.version,
    },
    tempRoot: root,
    imageRepresentation:
      "1,000 external URL references per heavy session in image-references.json; not DB bodies, not mounted/fetched by stage-1 UI. Image rendering/memory/request cost is NOT measured.",
    caveats: [
      "Local single-machine observations, not targets or guarantees",
      "SSE clone, timer wrappers, long-task observer and DB sampler add measurement overhead",
      "Timer callbacks measure instrumented JS timers, not all browser idle CPU work",
      "No screenshot/pixel paint timestamp; DOM plus requestAnimationFrame opportunity",
    ],
  };
  let server: Awaited<ReturnType<typeof startServer>> | undefined;
  let browserServer: Awaited<ReturnType<typeof chromium.launchServer>> | undefined;
  let browser: Awaited<ReturnType<typeof chromium.connect>> | undefined;
  let model: Awaited<ReturnType<typeof startLocalModelServer>> | undefined;
  let monitor: Worker | undefined;
  let safeToRemove = false;
  try {
    await mkdir(data);
    await mkdir(browserTemp);
    server = await startServer(data); // Real application initializes the current schema.
    model = await startLocalModelServer();
    const connection = await model.install(server.origin);
    const sessions = new SessionClient(server.origin);
    const ids: string[] = [];
    for (let index = 0; index < 6; index++) ids.push(await sessions.create());
    await server.stop();
    server = undefined;
    const path = join(data, "urd.sqlite");
    report.fixtures = seed(
      path,
      ids.slice(0, 5),
      // Internal storage uses CLR member names and numeric enums, not HTTP JSON.
      JSON.stringify({
        Connection: {
          Id: connection.id,
          Name: connection.name,
          Route: 2,
          Api: 1,
          Endpoint: connection.endpoint,
          Authentication: 0,
        },
        ModelId: "fixture-a",
        Family: 3,
        SupportsImages: false,
        ReasoningEffort: null,
      }),
    );
    await writeFile(
      join(root, "image-references.json"),
      JSON.stringify(
        {
          representation: report.imageRepresentation,
          sessions: ids.slice(0, 5).map((session) => ({
            session,
            images: Array.from({ length: 1000 }, (_, index) => ({
              entryGroup: index * 20,
              url: `https://images.invalid/${session}/${index}.webp`,
            })),
          })),
        },
        null,
        2,
      ),
    );
    report.queryBehavior = await queryPlans(path, ids[0]!);
    server = await startServer(data);
    const client = new SessionClient(server.origin);
    const beforeBrowser = await processMemory(server.pid);
    browserServer = await chromium.launchServer({
      headless: true,
      env: { ...process.env, TEMP: browserTemp, TMP: browserTemp, TMPDIR: browserTemp },
    });
    browser = await chromium.connect(browserServer.wsEndpoint());
    report.browserVersion = browser.version();
    const browserPid = browserServer.process().pid;
    assert.ok(browserPid);
    const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
    page.setDefaultTimeout(30_000);
    const pageErrors: string[] = [];
    report.pageErrors = pageErrors;
    page.on("pageerror", (error) => pageErrors.push(error.message));
    const requests: Request[] = [];
    page.on("request", (request) => {
      const url = new URL(request.url());
      if (url.origin === server!.origin.origin)
        requests.push({ at: epoch(), path: url.pathname, method: request.method() });
    });
    // Block accidental non-loopback traffic; image references deliberately never load.
    await page.route("**/*", (route) =>
      new URL(route.request().url()).origin === server!.origin.origin
        ? route.continue()
        : route.abort("blockedbyclient"),
    );
    await installProbe(page);
    const streamId = ids[5]!;
    const workspace = encodeWorkspace({
      version: 4,
      tabs: ids,
      activeId: streamId,
      chats: Object.fromEntries(ids.map((id) => [id, newChat(null)])),
      lastSelection: null,
    });
    await page.addInitScript((workspace) => {
      if (!sessionStorage.getItem("urd.workspace.v1"))
        sessionStorage.setItem("urd.workspace.v1", workspace);
    }, workspace);
    await page.goto(`${server.origin.href}#${streamId}`);
    await page.getByRole("textbox", { name: "Message" }).waitFor();
    await page.waitForFunction(() => document.querySelectorAll('[role="tab"]').length === 6);
    const clock = await calibrateClock(page);
    report.browserClock = {
      ...clock,
      method:
        "Browser epoch mapped to Node epoch using midpoint of narrowest of 20 enclosing round trips; uncertainty includes round-trip half-width plus 0.1 ms browser timestamp precision. Same-host monotonic clocks, no cold-disk claim.",
    };
    const memoryBefore = {
      browser: await processMemory(browserPid),
      backend: await processMemory(server.pid),
    };
    const switches: { kind: string; session: string; milliseconds: number }[] = [];
    report.tabSwitches = switches;
    async function switchHeavy(index: number, kind: string) {
      const session = ids[index]!;
      // Arm before clicking and timestamp the actual browser click, excluding Playwright's
      // actionability wait and protocol return overhead from the measured interval.
      const timing = page.evaluate(
        ({ session, marker }) =>
          new Promise<number>((resolve, reject) => {
            let start: number | undefined;
            const tab = document.querySelector(`#tab-${session}`)!;
            function clicked() {
              start = performance.timeOrigin + performance.now();
            }
            tab.addEventListener("click", clicked, { capture: true, once: true });
            const timer = setTimeout(() => {
              observer.disconnect();
              tab.removeEventListener("click", clicked, true);
              reject(new Error(`Tab did not render ${marker}`));
            }, 30_000);
            const observer = new MutationObserver(() => {
              if (start === undefined || tab.getAttribute("aria-selected") !== "true") return;
              if (
                ![...document.querySelectorAll("#chat-view .transcript .message-text")].some(
                  (node) => node.textContent?.includes(marker),
                )
              )
                return;
              observer.disconnect();
              clearTimeout(timer);
              requestAnimationFrame(() =>
                resolve(performance.timeOrigin + performance.now() - start!),
              );
            });
            observer.observe(document.body, {
              childList: true,
              characterData: true,
              subtree: true,
            });
          }),
        { session, marker: `baseline-heavy-${index}-latest` },
      );
      void timing.catch(() => {});
      await page.locator(`#tab-${session}`).click();
      try {
        switches.push({ kind, session, milliseconds: await timing });
        assert.equal(await page.locator(`#tab-${session}`).getAttribute("aria-selected"), "true");
      } catch (error) {
        report.tabSwitchFailure = {
          index,
          kind,
          selected: await page.locator(`#tab-${session}`).getAttribute("aria-selected"),
          textTail: (await page.locator("#chat-view").innerText()).slice(-2000),
          recentRequests: requests.slice(-10),
        };
        throw error;
      }
    }
    for (let index = 0; index < 5; index++) await switchHeavy(index, "cold-first-mount");
    for (let cycle = 0; cycle < 3; cycle++)
      for (let index = 0; index < 5; index++)
        await switchHeavy(index, `warm-return-cycle-${cycle + 1}`);
    report.tabSwitches = switches;
    report.memory = {
      backendBeforeBrowser: beforeBrowser,
      beforeTabCycles: memoryBefore,
      afterTabCycles: {
        browser: await processMemory(browserPid),
        backend: await processMemory(server.pid),
      },
      caveat:
        "Process-tree working sets, not JS heap; includes shared pages per process; five heavy tabs mounted/cycled three times, no forced GC",
    };
    await pause(1000);
    const idleStart = epoch();
    const idleBefore = await probe(page, clock);
    await pause(idleMs);
    const idleEnd = epoch();
    const idleAfter = await probe(page, clock);
    const beats = idleAfter.heartbeats.filter((at) => at >= idleStart && at <= idleEnd);
    report.idle = {
      durationMs: idleEnd - idleStart,
      scope:
        "One heavy session actively mounted, four heavy tabs and sixth idle session open but unmounted",
      sameOriginRequests: requests.filter((r) => r.at >= idleStart && r.at <= idleEnd),
      timerCallbacks: idleAfter.timers - idleBefore.timers,
      heartbeatTimes: beats,
      heartbeatIntervalsMs: beats.slice(1).map((at, index) => at - beats[index]!),
      events: idleAfter.events.filter((e) => e.at >= idleStart && e.at <= idleEnd),
      longTasks: summarizeTasks(idleAfter.tasks, idleStart, idleEnd),
    };

    await page.locator(`#tab-${streamId}`).click();
    await page.getByRole("textbox", { name: "Message" }).waitFor();
    await page.waitForFunction(
      () => !document.querySelector("#chat-view .transcript .message-text"),
    );
    await pause(1000); // Exclude the tab's watch handshake and initial reads from idle accounting.
    const settlingStart = epoch();
    const settlingBefore = await probe(page, clock);
    const settling = [];
    for (const targetMs of [0, 5000, 15_000, 30_000, 60_000]) {
      await pause(Math.max(0, settlingStart + targetMs - epoch()));
      const sampleStart = epoch();
      settling.push({
        targetMs,
        elapsedMs: sampleStart - settlingStart,
        browser: await processMemory(browserPid),
        backend: await processMemory(server.pid),
        measurementMs: epoch() - sampleStart,
      });
    }
    const settlingEnd = epoch();
    const settlingAfter = await probe(page, clock);
    const settlingBeats = settlingAfter.heartbeats.filter(
      (at) => at >= settlingStart && at < settlingEnd,
    );
    report.postTabCycleSettling = {
      scope:
        "After the original heavy-tab idle measurement, return to the initially empty sixth session (same view as beforeTabCycles); five heavy tabs remain open but unmounted. Normal GC only; working sets do not identify retained heap or prove a leak.",
      samples: settling,
      idle: {
        durationMs: settlingEnd - settlingStart,
        sameOriginRequests: requests.filter((r) => r.at >= settlingStart && r.at < settlingEnd),
        timerCallbacks: settlingAfter.timers - settlingBefore.timers,
        heartbeatTimes: settlingBeats,
        heartbeatIntervalsMs: settlingBeats.slice(1).map((at, index) => at - settlingBeats[index]!),
        events: settlingAfter.events.filter((e) => e.at >= settlingStart && e.at < settlingEnd),
      },
    };
    await pause(1000);
    const pushes = [];
    for (let sample = 0; sample < 3; sample++) {
      const prefix = `held-prefix-${sample}-${"p".repeat(80)}-`;
      const marker = `committed-marker-${randomUUID()}`;
      monitor = new Worker(new URL(import.meta.url), {
        workerData: { path, session: streamId, marker },
      });
      const currentMonitor = monitor;
      const committed = new Promise<Commit>((resolve, reject) => {
        currentMonitor.on("message", (message: Commit | { ready: boolean }) => {
          if (!("ready" in message)) resolve(message);
        });
        currentMonitor.once("error", reject);
        currentMonitor.once("exit", (code) => {
          if (code !== 0) reject(new Error(`DB monitor exited ${code}`));
        });
      });
      // Attach rejection handling before any asynchronous setup can fail.
      void committed.catch(() => {});
      await new Promise<void>((resolve, reject) => {
        currentMonitor.once("message", () => resolve());
        currentMonitor.once("error", reject);
      });
      model.enqueue({ text: prefix + marker, chunkSize: prefix.length, delayMs: 1000 });
      const snapshot = await client.getSnapshot(streamId);
      const renderedPromise = markerTime(page, marker, clock);
      void renderedPromise.catch(() => {});
      const receipt = await client.submitMessage(streamId, {
        submissionId: randomUUID(),
        expectedHistoryRevision: snapshot.historyRevision,
        text: `measure commit ${sample}`,
        llmSelection: {
          connection,
          modelId: "fixture-a",
          family: "other",
          supportsImages: false,
          contextWindow: null,
          reasoningEffort: null,
        },
      });
      assert.equal(receipt.acceptance, "accepted");
      const [renderedAt, commit] = await Promise.all([renderedPromise, committed]);
      pushes.push({
        lowerMs: Math.max(0, renderedAt - clock.uncertaintyMs - commit.positiveEnd),
        upperMs: renderedAt + clock.uncertaintyMs - commit.negativeStart,
        renderedAt,
        ...commit,
      });
      await monitor.terminate();
      monitor = undefined;
      await waitForIdle(client, streamId);
    }
    report.pushCommitToDOM = {
      method:
        "Separate readonly Node SQLite worker samples committed output every ~2ms. Commit is bracketed by last negative query start and first positive query end; DOM MutationObserver plus next rAF mapped to Node epoch with browserClock uncertainty included in the bracket. Not provider send time.",
      samples: pushes,
    };
    const streamText = Array.from(
      { length: 100 },
      (_, i) => `stream-${i.toString().padStart(3, "0")} ${"s".repeat(88)}\n`,
    ).join("");
    model.enqueue({ text: streamText, chunkSize: 100, delayMs: 100 });
    const snapshot = await client.getSnapshot(streamId);
    const finalRenderedPromise = markerTime(page, "stream-099", clock);
    void finalRenderedPromise.catch(() => {});
    const streamStart = epoch();
    await client.submitMessage(streamId, {
      submissionId: randomUUID(),
      expectedHistoryRevision: snapshot.historyRevision,
      text: "ten second stream",
      llmSelection: {
        connection,
        modelId: "fixture-a",
        family: "other",
        supportsImages: false,
        contextWindow: null,
        reasoningEffort: null,
      },
    });
    const firstRenderedAt = await markerTime(page, "stream-000", clock);
    const [finalRenderedAt] = await Promise.all([
      finalRenderedPromise,
      waitForIdle(client, streamId),
    ]);
    // Keep browser observation ordering in one clock domain; Node receipt can precede
    // a browser epoch timestamp by sub-millisecond clock-origin skew.
    const streamEnd =
      (await page.evaluate(() => performance.timeOrigin + performance.now())) + clock.offsetMs;
    await pause(200);
    const streamProbe = await probe(page, clock);
    const drainedAt = epoch();
    const runningNoticeAt = streamProbe.events.find(
      (event) =>
        event.name === "status" &&
        event.session === streamId &&
        event.status === "running" &&
        event.at >= streamStart,
    )?.at;
    assert.ok(runningNoticeAt !== undefined, "Missing running status during stream");
    const idleNoticeAt = streamProbe.events.find(
      (event) =>
        event.name === "status" &&
        event.session === streamId &&
        event.status === "idle" &&
        event.at > runningNoticeAt,
    )?.at;
    assert.ok(idleNoticeAt !== undefined, "Missing settlement status after stream");
    assert.ok(streamStart < firstRenderedAt && firstRenderedAt < finalRenderedAt);
    assert.ok(firstRenderedAt < idleNoticeAt && idleNoticeAt < drainedAt);
    assert.ok(finalRenderedAt <= streamEnd);
    const streamRequests = requests.filter((r) => r.at >= streamStart && r.at <= streamEnd);
    report.streaming = {
      durationMs: streamEnd - streamStart,
      scope:
        "Sixth session streams 100 chunks of 100 plain-text characters at 100 ms intervals while five heavy tabs remain open but unmounted; includes final settlement, no synthetic scroll input",
      sameOriginRequests: streamRequests.length,
      requestsPerSecond: streamRequests.length / ((streamEnd - streamStart) / 1000),
      byPath: Object.fromEntries(
        [...new Set(streamRequests.map((r) => r.path))].map((path) => [
          path,
          streamRequests.filter((r) => r.path === path).length,
        ]),
      ),
      observedWindows: {
        method:
          "Page GET request starts only; excludes Node waitForIdle polling. Active streaming runs from first chunk DOM-plus-rAF until the page observes SSE idle status. Settlement includes subsequent reads through the existing 200 ms drain. Client-observed phases, not exact provider/commit boundaries or pure settlement CPU time; final text may render after idle status. No pixel timestamps.",
        boundaries: {
          streamStart,
          runningNoticeAt,
          firstRenderedAt,
          finalRenderedAt,
          idleNoticeAt,
          streamEnd,
          drainedAt,
        },
        startup: summarizeGETs(requests, streamStart, firstRenderedAt),
        activeStreaming: summarizeGETs(requests, firstRenderedAt, idleNoticeAt),
        settlementAndDrain: summarizeGETs(requests, idleNoticeAt, drainedAt),
      },
      longTasksSupported: streamProbe.longTasksSupported,
      longTasks: summarizeTasks(streamProbe.tasks, streamStart, streamEnd),
    };
    await switchHeavy(0, "scroll-fixture-return");
    const scrollStart = epoch();
    await page.locator(".transcript").hover();
    for (let index = 0; index < 40; index++) {
      await page.mouse.wheel(0, index % 8 < 4 ? -600 : 600);
      await pause(100);
    }
    const scrollEnd = epoch();
    await pause(200);
    report.scrolling = {
      durationMs: scrollEnd - scrollStart,
      scope:
        "Heavy session's bounded stage-1 latest window; no 100k-row virtualization or automatic older-history traversal",
      longTasks: summarizeTasks((await probe(page, clock)).tasks, scrollStart, scrollEnd),
    };
    const http = [];
    for (const id of ids.slice(0, 5)) {
      const samples = [];
      for (let sample = 0; sample < 10; sample++) {
        const start = performance.now();
        const history = await client.getHistory(id, { mode: "latest", expectedHistoryRevision: 0 });
        samples.push(performance.now() - start);
        assert.equal(history.entries.length, 60);
        assert.equal(history.hasBefore, true);
      }
      http.push({ session: id, milliseconds: samples });
    }
    report.latestWindowHTTP = {
      scope:
        "Node typed-client loopback request through JSON decode; first sample and nine warm reads, not cold disk/cache",
      sessions: http,
    };
    report.pageErrors = pageErrors;
    assert.deepEqual(pageErrors, []);
    report.completed = true;
  } catch (error) {
    report.completed = false;
    report.blocker = error instanceof Error ? error.stack : String(error);
    if (error instanceof ProcessExitUnconfirmed) report.unconfirmedProcessExit = true;
    process.exitCode = 1;
  } finally {
    const cleanupErrors: string[] = [];
    // An earlier failure must not prevent shutdown of other owned processes.
    for (const cleanup of [
      () => monitor?.terminate(),
      () => browser?.close(),
      () => browserServer?.close(),
      () => server?.stop(),
      () => model?.close(),
    ]) {
      try {
        await cleanup();
      } catch (error) {
        cleanupErrors.push(String(error));
      }
    }
    safeToRemove = cleanupErrors.length === 0 && !report.unconfirmedProcessExit;
    if (cleanupErrors.length > 0) {
      report.cleanupBlockers = cleanupErrors;
      process.exitCode = 1;
    }
    console.log(JSON.stringify(report, null, 2));
    if (safeToRemove)
      await rm(root, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
    else console.error(`Owned temp data retained because cleanup did not confirm exit: ${root}`);
  }
}

async function waitForIdle(client: SessionClient, session: string) {
  const deadline = Date.now() + 30_000;
  while (Date.now() < deadline) {
    if (!(await client.getSnapshot(session)).unsettledExecutionId) return;
    await pause(50);
  }
  throw new Error("Fixture execution did not settle within 30 seconds");
}

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

if (isMainThread) await main();
else observeCommit();
