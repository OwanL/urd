import type { McpAuthentication } from "./McpServer.ts";

// Form fields exactly as edited; the request trims and nulls them for the wire.
export interface McpConnectForm {
  url: string;
  authentication: McpAuthentication;
  headerName: string;
  credential: string;
}

export interface McpConnectRequest {
  url: string;
  authentication: McpAuthentication;
  headerName: string | null;
  credential: string | null;
}

export type McpConnectProblem = "url" | "headerName" | "credential";

export interface McpConnectRejection {
  readonly field: McpConnectProblem;
  readonly message: string;
}

export function connectRejection(form: McpConnectForm): McpConnectRejection | null {
  if (!validEndpoint(form.url))
    return { field: "url", message: "Enter a valid HTTP or HTTPS URL." };
  if (form.authentication === "customHeader" && !form.headerName.trim())
    return { field: "headerName", message: "Enter a header name." };
  if (form.authentication !== "automatic" && !form.credential.trim())
    return { field: "credential", message: "Enter a credential." };
  return null;
}

export function connectRequest(form: McpConnectForm): McpConnectRequest {
  return {
    url: form.url.trim(),
    authentication: form.authentication,
    headerName: form.authentication === "customHeader" ? form.headerName.trim() : null,
    credential: form.authentication === "automatic" ? null : form.credential,
  };
}

function validEndpoint(value: string): boolean {
  try {
    const parsed = new URL(value.trim());
    return parsed.protocol === "http:" || parsed.protocol === "https:";
  } catch {
    return false;
  }
}
