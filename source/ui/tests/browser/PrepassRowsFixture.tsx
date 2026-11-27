import { render } from "preact";
import { PrepassRows, type ResponseStatusContext } from "../../chat/prepass/PrepassRows.tsx";
import type { PrepassSummary } from "../../sessions/transcript/Prepass.ts";

const context: ResponseStatusContext = {
  client: null as unknown as ResponseStatusContext["client"],
  sessionId: "test",
  modelNames: new Map(),
  visible: true,
};
const root = document.createElement("main");
document.body.append(root);

function show(state: string, length: number, key: string) {
  const rows: PrepassSummary[] = ["tools", "skills", "instructions", "title"].map((category) => ({
    boundaryId: "test",
    category,
    state,
    candidateCount: null,
    resultCount: null,
  }));
  render(
    <div key={key}>
      {Array.from({ length }, (_, i) => (
        <p key={i}>Earlier transcript row {i}</p>
      ))}
      <PrepassRows preparation={rows} context={context} historyRevision={1} />
      <p id="following">Following transcript row</p>
    </div>,
    root,
  );
}

Object.assign(window, { showPrepass: show });
