import "./base.css";
import { mountWorkspace } from "./workspace/WorkspaceMount.tsx";

const root = document.getElementById("app");
if (!root) throw new Error("Missing application root.");
mountWorkspace(root, { retainedPieces: 256, retainedTabs: 3 });
