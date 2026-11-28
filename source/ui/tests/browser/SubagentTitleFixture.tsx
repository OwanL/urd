import { render } from "preact";
import { useState } from "preact/hooks";
import "../../base.css";
import { SubagentNavigationContext } from "../../chat/subagents/SubagentNavigation.ts";
import { SubagentRow } from "../../chat/transcript/tool-calls/SubagentRow.tsx";
import { SessionClient } from "../../sessions/SessionClient.ts";
import type { TranscriptToolFacts } from "../../sessions/transcript/Messages.ts";

const client = new SessionClient(new URL(location.href));
const failed = new URLSearchParams(location.search).has("failed");
const tool: TranscriptToolFacts = {
  requestId: "request",
  callId: "call",
  name: "subagent",
  started: true,
  interrupted: false,
  background: false,
  batchedWithPrevious: false,
  batchedWithNext: false,
  outcome: failed ? "failed" : null,
  isClosed: failed,
  startedAt: null,
  endedAt: null,
};

function Fixture() {
  const [root, setRoot] = useState<HTMLDivElement | null>(null);
  const [opened, setOpened] = useState("");
  return (
    <div ref={setRoot} style={{ height: 120, overflow: "auto" }}>
      <SubagentNavigationContext.Provider value={(child) => setOpened(child.id)}>
        <SubagentRow
          client={client}
          sessionId="parent"
          historyRevision={0}
          tool={tool}
          presentation={{ kind: "call", heading: "Subagent", hasCompletion: false }}
          root={root}
          paused={false}
          modelNames={new Map([["fixture-model", "Claude Sonnet 4.6"]])}
        />
      </SubagentNavigationContext.Provider>
      {opened && <output>{opened}</output>}
    </div>
  );
}

render(<Fixture />, document.body);
