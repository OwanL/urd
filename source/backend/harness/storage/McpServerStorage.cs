using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Urd.Core.Storage;

public sealed record SavedMcpServer(Guid Id, string Name, string Url, string Authentication,
    string[] Tools, string? HeaderName, string? Credential, string? OAuthTokens) {
  public override string ToString() => $"MCP server {Id}";
}

// Stores MCP connection metadata and protected credential payloads without interpreting ciphertext.
public sealed class McpServerStorage {
  private readonly Database database;

  internal McpServerStorage(Database database) => this.database = database;
  private const string Columns = "server_id, name, url, authentication, header_name, credential, oauth_tokens, tools_json";

  public IReadOnlyList<SavedMcpServer> List() => database.Read(transaction => database.Query(transaction,
      $"SELECT {Columns} FROM mcp_servers ORDER BY rowid;", ReadServer));

  public SavedMcpServer? Find(Guid id) => database.Read(transaction => Read(transaction, id));

  public bool Save(SavedMcpServer server, bool existing) => database.Write(transaction => {
    if (existing)
      return database.Execute(transaction, """
          UPDATE mcp_servers SET name = $name, url = $url, authentication = $authentication,
              header_name = $headerName, credential = $credential, oauth_tokens = $oauthTokens, tools_json = $tools
          WHERE server_id = $id;
          """, Parameters(server)) != 0;

    return database.Execute(transaction, """
        INSERT INTO mcp_servers
            (server_id, name, url, authentication, header_name, credential, oauth_tokens, tools_json)
        VALUES ($id, $name, $url, $authentication, $headerName, $credential, $oauthTokens, $tools)
        ON CONFLICT(server_id) DO NOTHING;
        """, Parameters(server)) != 0;
  });

  public bool SaveOAuthTokens(Guid id, string tokens) => database.Write(transaction => database.Execute(transaction,
      "UPDATE mcp_servers SET oauth_tokens = $tokens WHERE server_id = $id;", ("$tokens", tokens), ("$id", id)) != 0);

  public bool Remove(Guid id) => database.Write(transaction =>
      database.Execute(transaction, "DELETE FROM mcp_servers WHERE server_id = $id;", ("$id", id)) != 0);

  private SavedMcpServer? Read(SqliteTransaction transaction, Guid id) => database.QuerySingleOrDefault(transaction,
      $"SELECT {Columns} FROM mcp_servers WHERE server_id = $id;", ReadServer, ("$id", id));

  private static SavedMcpServer ReadServer(SqliteDataReader row) => new(row.Guid("server_id"), row.String("name"),
      row.String("url"), row.String("authentication"), JsonSerializer.Deserialize<string[]>(row.String("tools_json"))!,
      row.NullableString("header_name"), row.NullableString("credential"), row.NullableString("oauth_tokens"));

  private static (string Name, object? Value)[] Parameters(SavedMcpServer server) => [
    ("$id", server.Id), ("$name", server.Name), ("$url", server.Url), ("$authentication", server.Authentication),
    ("$headerName", server.HeaderName), ("$credential", server.Credential), ("$oauthTokens", server.OAuthTokens),
    ("$tools", JsonSerializer.Serialize(server.Tools))
  ];
}
