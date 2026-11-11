import "./base.css";
import { mountMarkdownWorkspace } from "./app/MarkdownWorkspace.tsx";

const root = document.getElementById("app");
if (!root) throw new Error("Missing application root.");
mountMarkdownWorkspace(root, { retainedPieces: 256, retainedTabs: 3 });
