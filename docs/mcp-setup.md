# Connecting an MCP server

In the local browser, open **Settings → MCP → Add MCP** and enter the remote MCP server URL. Use HTTPS, or HTTP for a server running on loopback. Enter the MCP endpoint rather than the service homepage. URLs with embedded credentials, a query, or a fragment are not accepted.

Leave authentication on **Automatic** for public or OAuth-capable endpoints. If the server’s instructions specify a bearer token or a custom authentication header, choose that option and enter the credential in Settings. Urd supports OAuth servers that can register a client for its local browser callback; OAuth servers that require a separately provisioned client are not supported by this flow. MCP management is not available from the optional LAN listener.

**Connect** checks that Urd can reach the endpoint and list its tools, then saves the server in the local profile. Only servers you choose to connect and save are available to agents and subagents. They can call those tools in new turns, including in existing chats. Tools are paused while LAN hosting is active, and changes to saved servers apply to new turns. Stopping a turn or removing a server may not undo effects of calls already in progress. Save only servers you trust. Urd passes text and structured results back to agents; other content types are not supported yet.

The server list is not an online status monitor. Open an entry to inspect the last discovered tool list, reconnect, or remove it. Removing a connection deletes the local record; revoke credentials with the service if you want to invalidate them.
