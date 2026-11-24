import { render } from "preact";
import { BrowserViewer } from "../../chat/browser/BrowserViewer.tsx";
import {
  BrowserPageStream,
  type BrowserPageStreamHandlers,
} from "../../chat/browser/BrowserPageStream.ts";
import "../../base.css";
import "../../chat/chat.css";
import "../../chat/transcript/transcript.css";
import "../../chat/composer/composer.css";

window.fetch = async () =>
  new Response(
    JSON.stringify({
      pages: [
        {
          id: "page",
          ownerId: "owner",
          title: "Example",
          url: "https://example.com",
          active: true,
        },
      ],
    }),
    {
      headers: { "Content-Type": "application/json" },
    },
  );

BrowserPageStream.prototype.send = () => true;
BrowserPageStream.prototype.close = () => {};
BrowserPageStream.prototype.open = function () {
  const handlers = (this as unknown as { handlers: BrowserPageStreamHandlers }).handlers;
  handlers.stateChange("open", null);
  const image = document.createElement("canvas");
  image.width = 1280;
  image.height = 720;
  image.getContext("2d")!.fillRect(0, 0, image.width, image.height);
  image.toBlob((blob) => handlers.frame(blob!));
};

render(
  <div style={{ display: "flex", height: "100%" }}>
    <div class="chat-view">
      <BrowserViewer
        sessionId="session"
        active
        refresh={0}
        browserRefresh={0}
        openRequest={null}
        onOwnersChange={() => {}}
        onReturnToQuestion={undefined}
      >
        {(launcher) => (
          <>
            <div class="transcript-area">
              <div class="transcript" />
            </div>
            <div class="composer">
              {launcher && <div class="browser-launcher-slot">{launcher}</div>}
              <textarea class="composer-input" aria-label="Message" />
            </div>
          </>
        )}
      </BrowserViewer>
    </div>
  </div>,
  document.body,
);
