import { render } from "preact";
import { BrowserPageView } from "../../chat/browser/BrowserPageView.tsx";
import { BrowserPageStream } from "../../chat/browser/BrowserPageStream.ts";
import type { BrowserPageCommand } from "../../chat/browser/BrowserPage.ts";

const commands: BrowserPageCommand[] = [];
Object.assign(window, { viewerCommands: commands });
BrowserPageStream.prototype.open = function () {
  Object.assign(window, { viewerReady: true });
};
BrowserPageStream.prototype.close = function () {};
BrowserPageStream.prototype.send = function (command) {
  commands.push(command);
  return true;
};

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
