import { render } from "preact";
import { useState } from "preact/hooks";
import { SessionClient } from "../../../sessions/SessionClient.ts";
import { useSessionSnapshot } from "../../../sessions/useSessionSnapshot.ts";
import { Transcript } from "../../../chat/transcript/Transcript.tsx";
import { RichMessageBody } from "../../../chat/transcript/RichMessageBody.tsx";
import type { TranscriptDisplay } from "../../../chat/transcript/TranscriptDisplay.ts";
import type { TranscriptNavigationState } from "../../../chat/transcript/navigation/TranscriptNavigationStorage.ts";
import { MarkdownClient } from "../../../markdown-rendering/MarkdownClient.ts";
import "../../../base.css";

const query = new URLSearchParams(location.search);
const sessionId = query.get("session")!;
const api = new URL(query.get("api")!);
const client = new SessionClient(api);
const navigation = new Map<string, TranscriptNavigationState>();
const display: TranscriptDisplay = {
  MessageBody: RichMessageBody,
  client: new MarkdownClient(api),
  retainedPieces: 256,
  retainedTabs: 3,
  release(work) {
    void work.catch((error: unknown) => globalThis.reportError(error));
  },
};
const ignore = () => {};

function Conversation({ notice }: { notice: string }) {
  const { snapshot, error } = useSessionSnapshot(
    client,
    sessionId,
    0,
    null,
    false,
    ignore,
    notice,
    true,
    false,
  );
  return (
    <section class="fixture-panel">
      {error && <p role="alert">{error}</p>}
      <Transcript
        client={client}
        history={null}
        historyRevision={snapshot?.historyRevision ?? null}
        notice={notice}
        display={display}
        streamId="browser-fixture"
        sessionId={sessionId}
        visible
        position={undefined}
        onPositionChange={ignore}
        transcriptPosition={navigation.get(sessionId)}
        onTranscriptPositionChange={(state) => navigation.set(sessionId, state)}
      />
    </section>
  );
}

function Harness() {
  const [active, setActive] = useState(true);
  const [notice, setNotice] = useState(0);
  return (
    <>
      <nav aria-label="Fixture controls">
        <button type="button" onClick={() => setActive(true)}>
          Conversation tab
        </button>
        <button type="button" onClick={() => setActive(false)}>
          Other tab
        </button>
        <button type="button" onClick={() => setNotice((value) => value + 1)}>
          Publish change notice
        </button>
      </nav>
      {active ? <Conversation notice={String(notice)} /> : <p>Other conversation</p>}
    </>
  );
}

render(<Harness />, document.getElementById("app")!);
