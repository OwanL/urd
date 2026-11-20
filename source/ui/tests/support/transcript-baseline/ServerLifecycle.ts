import type { SessionClient } from "../../../sessions/SessionClient.ts";
import { ServerProcess, ServerProcessNotStoppedError } from "../ServerProcess.ts";

const pause = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

export class ProcessExitUnconfirmed extends Error {}

export async function startServer(root: string) {
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

export async function waitForIdle(client: SessionClient, session: string) {
  const deadline = Date.now() + 30_000;
  while (Date.now() < deadline) {
    if (!(await client.getSnapshot(session)).unsettledExecutionId) return;
    await pause(50);
  }
  throw new Error("Fixture execution did not settle within 30 seconds");
}
