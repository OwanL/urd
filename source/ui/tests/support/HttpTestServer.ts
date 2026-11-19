import { createServer } from "node:http";
import type { IncomingHttpHeaders, ServerResponse } from "node:http";
import { once } from "node:events";

export interface HttpRequest {
  method: string;
  url: string;
  headers: IncomingHttpHeaders;
  body: string;
}

export async function startHttpServer(
  respond: (request: HttpRequest, response: ServerResponse) => void,
) {
  const requests: HttpRequest[] = [];
  const server = createServer((request, response) => {
    void (async () => {
      const chunks: Buffer[] = [];
      for await (const chunk of request) chunks.push(Buffer.from(chunk));

      const received: HttpRequest = {
        method: request.method ?? "",
        url: request.url ?? "",
        headers: request.headers,
        body: Buffer.concat(chunks).toString("utf8"),
      };
      requests.push(received);
      respond(received, response);
    })().catch((error: unknown) => {
      response.destroy(error instanceof Error ? error : undefined);
    });
  });

  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  const address = server.address();
  if (address === null || typeof address === "string") throw new Error("Expected a TCP address");

  return {
    origin: new URL(`http://127.0.0.1:${address.port}`),
    requests,
    async close() {
      const closed = new Promise<void>((resolve, reject) => {
        server.close((error) => (error ? reject(error) : resolve()));
      });
      server.closeAllConnections();
      await closed;
    },
  };
}
