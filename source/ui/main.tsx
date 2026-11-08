import { render } from "preact";
import { Workspace } from "./app/Workspace.tsx";

const root = document.getElementById("app");
if (!root) throw new Error("Missing application root.");
render(<Workspace />, root);
