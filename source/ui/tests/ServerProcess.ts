import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { access, mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { createInterface } from "node:readline";
import { fileURLToPath } from "node:url";

const assembly = fileURLToPath(new URL("../../hosting/server/bin/Release/net10.0/Urd.Server.dll", import.meta.url));

// Owns only this child and its temporary data. Termination is not a graceful shutdown test.
export class ServerProcess {
  readonly origin: URL;
  private readonly child: ChildProcessWithoutNullStreams;
  private readonly closed: Promise<void>;
  private readonly dataRoot: string;

  private constructor(origin: URL, child: ChildProcessWithoutNullStreams, closed: Promise<void>, dataRoot: string) {
    this.origin = origin;
    this.child = child;
    this.closed = closed;
    this.dataRoot = dataRoot;
  }

  static async start(): Promise<ServerProcess> {
    await access(assembly);
    const dataRoot = await mkdtemp(join(tmpdir(), "urd-ui-integration-"));
    let child: ChildProcessWithoutNullStreams;
    try {
      child = spawn(process.env.DOTNET_HOST_PATH ?? "dotnet", [assembly, "--data-root", dataRoot, "--port", "0"], {
        stdio: "pipe",
        windowsHide: true,
      });
    } catch (error) {
      await rm(dataRoot, { recursive: true, force: true });
      throw error;
    }

    const closed = new Promise<void>(resolve => child.once("close", () => resolve()));
    const lines = createInterface({ input: child.stdout });
    let errors = "";
    child.stderr.setEncoding("utf8").on("data", (chunk: string) => {
      errors = (errors + chunk).slice(-8_192);
    });
    child.on("error", error => {
      errors = (errors + String(error)).slice(-8_192);
    });
    child.stdin.end();
    try {
      const origin = await new Promise<URL>((resolve, reject) => {
        const timer = setTimeout(() => finish(new Error("Server readiness timed out")), 15_000);
        const onError = (error: Error) => finish(error);
        const onClose = (code: number | null, signal: NodeJS.Signals | null) =>
          finish(new Error(`Server exited before readiness (${code ?? signal})`));
        const onLine = (line: string) => {
          let ready: unknown;
          try {
            ready = JSON.parse(line);
          } catch {
            return;
          }
          if (typeof ready !== "object" || ready === null || !("event" in ready) || ready.event !== "urd-ready")
            return;
          try {
            if (!("origin" in ready) || typeof ready.origin !== "string")
              throw new Error("Readiness did not include an origin");
            const origin = new URL(ready.origin);
            if (origin.protocol !== "http:" || origin.hostname !== "127.0.0.1" || !origin.port ||
                origin.pathname !== "/" || origin.search || origin.hash || origin.username || origin.password)
              throw new Error(`Unexpected server origin: ${origin}`);
            finish(undefined, origin);
          } catch (error) {
            finish(error);
          }
        };
        function finish(error?: unknown, origin?: URL) {
          clearTimeout(timer);
          child.off("error", onError);
          child.off("close", onClose);
          lines.off("line", onLine);
          if (origin)
            resolve(origin);
          else
            reject(new Error(`${String(error)}\n${errors}`));
        }
        child.once("error", onError);
        child.once("close", onClose);
        lines.on("line", onLine);
      });
      return new ServerProcess(origin, child, closed, dataRoot);
    } catch (error) {
      await ServerProcess.reap(child, closed);
      await rm(dataRoot, { recursive: true, force: true, maxRetries: 3, retryDelay: 100 });
      throw error;
    } finally {
      lines.close();
    }
  }

  async stop(): Promise<void> {
    await ServerProcess.reap(this.child, this.closed);
    await rm(this.dataRoot, { recursive: true, force: true, maxRetries: 3, retryDelay: 100 });
  }

  private static async reap(child: ChildProcessWithoutNullStreams, closed: Promise<void>): Promise<void> {
    // Kill only our child; never remove its data until close confirms it has been reaped.
    if (child.exitCode === null && child.signalCode === null && child.pid !== undefined)
      child.kill("SIGKILL");
    let timer: ReturnType<typeof setTimeout> | undefined;
    try {
      await Promise.race([
        closed,
        new Promise<never>((_, reject) => {
          timer = setTimeout(() => reject(new Error("Owned server did not exit; temporary data retained")), 5_000);
        }),
      ]);
    } finally {
      clearTimeout(timer);
    }
  }
}
