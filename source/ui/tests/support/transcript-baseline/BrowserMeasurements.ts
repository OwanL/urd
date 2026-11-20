import { execFile } from "node:child_process";
import { promisify } from "node:util";
import type { Page } from "playwright";

const runFile = promisify(execFile);
const epoch = () => performance.timeOrigin + performance.now();

export type Task = { at: number; duration: number };
export type BaselineRequest = { at: number; path: string; method: string };
export type Probe = {
  timers: number;
  heartbeats: number[];
  events: { name: string; at: number; session?: string; status?: string }[];
  tasks: Task[];
  longTasksSupported: boolean;
};
export type Memory = { workingSetBytes: number; processes: number; method: string };
export type Clock = { offsetMs: number; uncertaintyMs: number; roundTripMs: number };

export async function processMemory(pid: number): Promise<Memory> {
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

export async function installProbe(page: Page) {
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

export async function calibrateClock(page: Page): Promise<Clock> {
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

export async function probe(page: Page, clock: Clock) {
  const value = await page.evaluate(() => (window as unknown as { __baseline: Probe }).__baseline);
  return {
    ...value,
    heartbeats: value.heartbeats.map((at) => at + clock.offsetMs),
    events: value.events.map((event) => ({ ...event, at: event.at + clock.offsetMs })),
    tasks: value.tasks.map((task) => ({ ...task, at: task.at + clock.offsetMs })),
  };
}

export async function markerTime(page: Page, marker: string, clock: Clock) {
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

export function summarizeTasks(tasks: Task[], start: number, end: number) {
  const values = tasks
    .filter((task) => task.at >= start && task.at < end)
    .map((task) => task.duration);
  return {
    count: values.length,
    totalMs: values.reduce((sum, value) => sum + value, 0),
    maxMs: Math.max(0, ...values),
  };
}

export function summarizeGETs(requests: BaselineRequest[], start: number, end: number) {
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
