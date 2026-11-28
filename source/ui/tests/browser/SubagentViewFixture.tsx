import { render } from "preact";
import { useState } from "preact/hooks";
import { SubagentView } from "../../chat/subagents/SubagentView.tsx";
import { SessionClient } from "../../sessions/SessionClient.ts";
import "../../base.css";

const client = new SessionClient(new URL(location.href));
const child = { id: "00000000-0000-4000-8000-000000000002", parentId: "parent", title: "Child" };

function Fixture() {
  const [open, setOpen] = useState(false);
  return open ? (
    <SubagentView
      client={client}
      path={[child]}
      rootId={child.parentId}
      rootTitle="Parent chat"
      onNavigate={(path) => setOpen(path.length > 0)}
      display={undefined}
      streamId={null}
      connected
      notice="1"
    />
  ) : (
    <button type="button" onClick={() => setOpen(true)}>
      Open child
    </button>
  );
}

document.body.style.display = "flex";
render(<Fixture />, document.body);
