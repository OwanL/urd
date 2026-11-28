import { render } from "preact";
import { useState } from "preact/hooks";
import { SessionIndicators } from "../../chat/composer/SessionIndicators.tsx";
import { usePromptCache } from "../../chat/composer/usePromptCache.ts";
import "../../base.css";

function Fixture() {
  const [refresh, setRefresh] = useState(0);
  const [compactions, setCompactions] = useState(0);
  const sessionId = "00000000-0000-4000-8000-000000000001";
  const promptCache = usePromptCache(sessionId, true, refresh);
  return (
    <>
      <button onClick={() => setRefresh((n) => n + 1)}>Refresh</button>
      <output>Compactions: {compactions}</output>
      <SessionIndicators
        contextUsage={{ usedTokens: 1000, source: "measured" }}
        contextWindow={128000}
        workingTime={null}
        active
        fenced={false}
        sessionId={sessionId}
        usageActive
        usageBusy={false}
        usageRefresh={refresh}
        promptCache={promptCache}
        compaction={{
          canCompact: true,
          status: null,
          message: null,
          unavailableReason: null,
          onCheck: undefined,
          onCancel: undefined,
          onCompact: () => setCompactions((n) => n + 1),
        }}
      />
    </>
  );
}
render(<Fixture />, document.body);
