import { randomUUID } from "node:crypto";
import type { ServerResponse } from "node:http";
import { requestJson } from "../http/requestJson.ts";
import { ProviderClient } from "../providers/ProviderClient.ts";
import type { ProviderConnection } from "../providers/ProviderConnection.ts";
import { startHttpServer } from "./HttpTestServer.ts";

export interface FixtureReply {
  text: string;
  delayMs?: number;
  chunkSize?: number;
  usage?: { inputTokens: number; outputTokens: number };
}

export interface ModelRequest {
  model: string;
  reasoning_effort?: string;
  messages: unknown[];
}

// A loopback-only, credential-free provider fixture. Urd still uses its real SDK,
// provider manager, execution driver, SQLite and HTTP routes; no production bypass.
export async function startLocalModelServer(onShutdown?: () => void) {
  const replies: FixtureReply[] = [];
  const calls: { request: ModelRequest; cancelled: boolean }[] = [];
  const timers = new Set<ReturnType<typeof setTimeout>>();
  const server = await startHttpServer((request, response) => {
    if (request.method === "GET" && request.url === "/v1/models") {
      json(response, {
        object: "list",
        data: ["fixture-a", "fixture-b"].map((id) => ({
          id,
          object: "model",
          created: 1,
          owned_by: "offline-fixture",
        })),
      });
      return;
    }
    // Test controls belong only to this fixture process, never to Urd's server.
    if (request.method === "POST" && request.url === "/__shutdown" && onShutdown) {
      json(response, {});
      onShutdown();
      return;
    }
    if (request.method === "GET" && request.url === "/__requests") {
      json(response, calls);
      return;
    }
    if (request.method === "POST" && request.url === "/__next") {
      const reply = JSON.parse(request.body) as FixtureReply;
      if (
        typeof reply.text !== "string" ||
        reply.text.length > 128_000 ||
        (reply.delayMs !== undefined &&
          (!Number.isInteger(reply.delayMs) || reply.delayMs < 0 || reply.delayMs > 1_000)) ||
        (reply.chunkSize !== undefined &&
          (!Number.isInteger(reply.chunkSize) || reply.chunkSize < 1 || reply.chunkSize > 4_096))
      ) {
        response.writeHead(400).end();
        return;
      }
      // Replace an unconsumed control reply: Stop may prevent a request entirely.
      replies.splice(0, replies.length, reply);
      json(response, {});
      return;
    }
    if (request.method !== "POST" || request.url !== "/v1/chat/completions") {
      response.writeHead(404).end();
      return;
    }
    const body = JSON.parse(request.body) as ModelRequest;
    const call = { request: body, cancelled: false };
    calls.push(call);
    const reply = replies.shift() ?? { text: "Hello from Urd." };
    const points = [...reply.text];
    const chunkSize = reply.chunkSize ?? 80;
    const id = `chatcmpl-${randomUUID()}`;
    let offset = 0;
    let finished = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    response.writeHead(200, { "Content-Type": "text/event-stream", "Cache-Control": "no-cache" });
    response.flushHeaders();
    response.once("close", () => {
      call.cancelled = !finished;
      if (timer !== undefined) {
        clearTimeout(timer);
        timers.delete(timer);
      }
    });

    function frame(delta: object, finishReason: string | null = null) {
      response.write(
        `data: ${JSON.stringify({
          id,
          object: "chat.completion.chunk",
          created: 1,
          model: body.model,
          choices: [{ index: 0, delta, finish_reason: finishReason }],
        })}\n\n`,
      );
    }
    function emit() {
      if (timer !== undefined) timers.delete(timer);
      if (response.destroyed) return;
      if (offset < points.length) {
        frame({ content: points.slice(offset, offset + chunkSize).join("") });
        offset += chunkSize;
        if (reply.delayMs) {
          timer = setTimeout(emit, reply.delayMs);
          timers.add(timer);
          return;
        }
        while (offset < points.length) {
          frame({ content: points.slice(offset, offset + chunkSize).join("") });
          offset += chunkSize;
        }
      }
      frame({}, "stop");
      if (reply.usage) {
        response.write(
          `data: ${JSON.stringify({
            id,
            object: "chat.completion.chunk",
            created: 1,
            model: body.model,
            choices: [],
            usage: {
              prompt_tokens: reply.usage.inputTokens,
              completion_tokens: reply.usage.outputTokens,
              total_tokens: reply.usage.inputTokens + reply.usage.outputTokens,
            },
          })}\n\n`,
        );
      }
      finished = true;
      response.end("data: [DONE]\n\n");
    }
    frame({ role: "assistant" });
    emit();
  });

  return {
    origin: server.origin,
    calls,
    enqueue(reply: FixtureReply) {
      replies.push(reply);
    },
    async install(urdOrigin: URL): Promise<ProviderConnection> {
      const connection = await requestJson<ProviderConnection>(
        new URL("/api/providers/custom", urdOrigin),
        "POST",
        {
          name: "Offline fixture",
          api: "openAIChatCompletions",
          endpoint: new URL("/v1/", server.origin).href,
          authentication: "none",
        },
      );
      await new ProviderClient(urdOrigin).refreshModels(connection.id);
      return connection;
    },
    async close() {
      for (const timer of timers) clearTimeout(timer);
      timers.clear();
      await server.close();
    },
  };
}

function json(response: ServerResponse, body: unknown) {
  response.writeHead(200, { "Content-Type": "application/json" });
  response.end(JSON.stringify(body));
}
