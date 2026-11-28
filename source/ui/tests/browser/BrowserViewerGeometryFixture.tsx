import { render } from "preact";
import { useEffect, useState } from "preact/hooks";
import { BrowserViewer } from "../../chat/browser/BrowserViewer.tsx";
import {
  BrowserPageStream,
  type BrowserPageStreamHandlers,
} from "../../chat/browser/BrowserPageStream.ts";
import "../../base.css";
import "../../chat/chat.css";
import "../../chat/transcript/transcript.css";
import "../../chat/composer/composer.css";

window.fetch = async (_input, init) => {
  if (init?.method === "DELETE") return new Response(null, { status: 204 });
  const body =
    init?.method === "POST"
      ? { id: "vault", ownerId: "session", title: "Bitwarden", url: "", active: false }
      : {
          bitwarden: location.hash.startsWith("#bitwarden"),
          pages:
            location.hash === "#bitwarden-empty"
              ? []
              : [
                  {
                    id: "page",
                    ownerId: "owner",
                    title: "Example",
                    url: "https://example.com",
                    active: true,
                  },
                ],
        };
  return Response.json(body);
};

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

function Fixture() {
  const [browserRefresh, setBrowserRefresh] = useState(0);
  useEffect(() => {
    const refresh = () => setBrowserRefresh((count) => count + 1);
    window.addEventListener("hashchange", refresh);
    return () => window.removeEventListener("hashchange", refresh);
  }, []);

  return (
    <div style={{ display: "flex", height: "100%" }}>
      <div class="chat-view">
        <BrowserViewer
          sessionId="session"
          active
          refresh={0}
          browserRefresh={browserRefresh}
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
    </div>
  );
}

render(<Fixture />, document.body);
