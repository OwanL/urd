import { startLocalModelServer } from "./LocalModelServer.ts";
import { ServerProcess } from "./ServerProcess.ts";

// Explicit development/test host only: all state is temporary and every model
// request goes to our owned loopback fixture. No saved account or real model is used.
let requestShutdown = () => {};
const stopping = new Promise<void>((resolve) => {
  requestShutdown = resolve;
});
process.once("SIGINT", requestShutdown);
process.once("SIGTERM", requestShutdown);
const model = await startLocalModelServer(requestShutdown);
let server: ServerProcess | undefined;
try {
  server = await ServerProcess.start();
  const connection = await model.install(server.origin);
  console.log(
    JSON.stringify({
      event: "fixture-ready",
      origin: server.origin.origin,
      modelOrigin: model.origin.origin,
      connection,
    }),
  );
  await stopping;
} finally {
  try {
    await server?.stop();
  } finally {
    await model.close();
    process.off("SIGINT", requestShutdown);
    process.off("SIGTERM", requestShutdown);
  }
}
