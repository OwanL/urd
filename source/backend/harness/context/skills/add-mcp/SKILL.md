---
name: add-mcp
description: "Use when the user wants to add a remote MCP server connection."
---

# Add an MCP connection

Find the server's official remote MCP endpoint and propose it with `propose_mcp_connection` for the user to review. Use HTTPS, or HTTP only for a loopback server. Do not include credentials, query parameters or fragments in the endpoint. Never ask the user to send secrets in chat. The proposal does not connect or save anything; the user completes setup in Urd.
