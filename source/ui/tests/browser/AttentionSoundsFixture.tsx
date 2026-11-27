import { render } from "preact";
import { SessionClient } from "../../sessions/SessionClient.ts";
import { useAttentionSounds } from "../../workspace/useAttentionSounds.ts";
import { useSessionEvents } from "../../workspace/useSessionEvents.ts";
import { emptyWorkspace } from "../../workspace/WorkspaceState.ts";
import type { WorkspaceCommandAccess } from "../../workspace/WorkspaceCommands.ts";

const tab = "chat";
const open = [tab];
const client = new SessionClient(new URL(location.href));
const access: WorkspaceCommandAccess = {
  read: emptyWorkspace,
  save: () => true,
  updateChat: () => true,
  canPersist: () => true,
};
const noop = () => {};
const isClosed = () => false;
const frequencies: number[] = [];
const registrations: unknown[] = [];
let stream: ReadableStreamDefaultController<Uint8Array>;
let streamCount = 0;
function send(name: string, data: object) {
  stream.enqueue(new TextEncoder().encode(`event: ${name}\ndata: ${JSON.stringify(data)}\n\n`));
}

const originalStart = OscillatorNode.prototype.start;
OscillatorNode.prototype.start = function (when?: number) {
  frequencies.push(this.frequency.value);
  originalStart.call(this, when);
};

const fetchFromServer = window.fetch.bind(window);
window.fetch = async (input, init) => {
  const path = new URL(String(input), location.href).pathname;
  if (path === "/api/events") {
    const body = new ReadableStream<Uint8Array>({
      start(controller) {
        stream = controller;
        streamCount++;
        send("connected", { stream: `stream-${streamCount}`, boot: "host" });
      },
    });
    return new Response(body, { headers: { "Content-Type": "text/event-stream" } });
  }
  if (path.startsWith("/api/events/")) {
    registrations.push(JSON.parse(String(init?.body)));
    return new Response(null, { status: 204 });
  }
  if (path === "/api/sessions") return Response.json({ sessionIds: [tab], nextCursor: null });
  return fetchFromServer(input, init);
};

function Fixture() {
  const sounds = useAttentionSounds();
  const events = useSessionEvents(
    client,
    access,
    noop,
    isClosed,
    true,
    open,
    tab,
    noop,
    sounds.play,
  );
  return (
    <>
      <button type="button">Use Urd</button>
      <label>
        <input
          type="checkbox"
          checked={sounds.enabled}
          onChange={(event) => sounds.setEnabled(event.currentTarget.checked)}
        />
        Sounds
      </label>
      <output>{events.provisioning}</output>
    </>
  );
}

export const fixture = {
  send,
  frequencies,
  registrations,
  setHidden(hidden: boolean) {
    Object.defineProperty(document, "hidden", { configurable: true, value: hidden });
    document.dispatchEvent(new Event("visibilitychange"));
  },
  get streamCount() {
    return streamCount;
  },
};

declare global {
  interface Window {
    attentionFixture: typeof fixture;
  }
}
window.attentionFixture = fixture;
render(<Fixture />, document.body);
