import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { cpus, freemem, hostname, platform, release, tmpdir, totalmem } from "node:os";
import { join } from "node:path";
import { Worker } from "node:worker_threads";
import { chromium } from "playwright";
import { encodeWorkspace } from "../../../workspace/WorkspaceState.ts";
import { newChat } from "../../../chat/ChatState.ts";
import { SessionClient } from "../../../sessions/SessionClient.ts";
import { startLocalModelServer } from "../LocalModelServer.ts";
import { seed, queryPlans } from "./DatabaseFixture.ts";
import {
  calibrateClock,
  installProbe,
  markerTime,
  processMemory,
  probe,
  summarizeGETs,
  summarizeTasks,
  type BaselineRequest,
} from "./BrowserMeasurements.ts";
import { ProcessExitUnconfirmed, startServer, waitForIdle } from "./ServerLifecycle.ts";
import type { Commit } from "./CommitObserver.ts";

const pause = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));
const epoch = () => performance.timeOrigin + performance.now();
const idleMs = 16_000;

export async function runTranscriptBaseline() {
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
    const requests: BaselineRequest[] = [];
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
      version: 8,
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
      monitor = new Worker(new URL("./CommitObserver.ts", import.meta.url), {
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
