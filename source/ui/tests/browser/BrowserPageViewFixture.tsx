import "../../chat/browser/browser-viewer.css";
import { render } from "preact";
import { BrowserPageView } from "../../chat/browser/BrowserPageView.tsx";
import { BrowserPageStream } from "../../chat/browser/BrowserPageStream.ts";
import type { BrowserPage, BrowserPageCommand } from "../../chat/browser/BrowserPage.ts";
import type { BrowserPageStreamHandlers } from "../../chat/browser/BrowserPageStream.ts";

const commands: BrowserPageCommand[] = [];
Object.assign(window, { viewerCommands: commands });

// Prototype stubs keep the real constructor so tests reach the handlers and
// drive connection state and frame delivery directly.
BrowserPageStream.prototype.send = function (command) {
  commands.push(command);
  return true;
};
BrowserPageStream.prototype.close = function () {};
BrowserPageStream.prototype.open = function () {
  const handlers = (this as unknown as { handlers: BrowserPageStreamHandlers }).handlers;
  Object.assign(window, {
    viewerReady: true,
    viewerStream: {
      open: () => handlers.stateChange("open", null),
      close: () => handlers.stateChange("closed", "Connection lost."),
      frame: (blob: Blob) => handlers.frame(blob),
    },
  });
  handlers.stateChange("open", null);
};

// A controllable stand-in for createImageBitmap so tests can stall, count and
// release decodes like a busy page would.
const decodeImage = globalThis.createImageBitmap.bind(globalThis);
let decodeRequests = 0;
let decodesInFlight = 0;
let peakConcurrent = 0;
let stallsRemaining = 0;
const stallResolvers: Array<() => void> = [];

Object.assign(globalThis, {
  createImageBitmap: (blob: Blob) => {
    decodeRequests += 1;
    decodesInFlight += 1;
    peakConcurrent = Math.max(peakConcurrent, decodesInFlight);
    let gate: Promise<void> = Promise.resolve();
    if (stallsRemaining > 0) {
      stallsRemaining -= 1;
      gate = new Promise<void>((resolve) => stallResolvers.push(resolve));
    }
    const decoded = gate.then(() => decodeImage(blob));
    return decoded.finally(() => {
      decodesInFlight -= 1;
    });
  },
  viewerDecode: {
    stallNext: () => {
      stallsRemaining += 1;
    },
    release: () => {
      stallsRemaining = 0;
      for (const resolve of stallResolvers.splice(0)) resolve();
    },
    reset: () => {
      decodeRequests = 0;
      decodesInFlight = 0;
      peakConcurrent = 0;
      stallsRemaining = 0;
      stallResolvers.length = 0;
    },
    stats: () => ({ decodeRequests, inFlight: decodesInFlight, peakConcurrent }),
  },
  viewerShowPage: (page: BrowserPage) =>
    render(<BrowserPageView sessionId="session" page={page} enabled />, document.body),
});

render(
  <BrowserPageView
    sessionId="session"
    page={{
      id: "page",
      ownerId: "owner",
      title: "Example",
      url: "https://example.com",
      active: true,
    }}
    enabled
  />,
  document.body,
);
