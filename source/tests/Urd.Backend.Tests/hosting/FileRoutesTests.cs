using System.Net;
using System.Text.Json;
using Urd.Backend.Tests.Support;
using Urd.Server;
using Xunit;
using static Urd.Backend.Tests.Support.HttpTest;

namespace Urd.Backend.Tests.Hosting;

public sealed class FileRoutesTests {
  [Fact]
  public async Task DirectoryBrowsingListsRootsFoldersFiltersAndReportsErrors() {
    using var database = new TemporaryDatabase();
    string ownedDirectory = Path.Combine(Path.GetDirectoryName(database.Path)!, "folder-browser");
    string parent = Path.Combine(ownedDirectory, "parent");
    string alpha = Path.Combine(parent, "Alpha");
    Directory.CreateDirectory(Path.Combine(alpha, "nested"));
    Directory.CreateDirectory(Path.Combine(parent, "beta"));
    await using var server = new RunningServer(new(Path.GetDirectoryName(database.Path)!, 0));
    await server.WaitForReadyAsync();

    using (HttpResponseMessage roots = await RequestAsync(server.Client, HttpMethod.Get,
        "/api/files/directories")) {
      Assert.Equal(HttpStatusCode.OK, roots.StatusCode);
      AssertPrivateResponse(roots);
      using JsonDocument response = JsonDocument.Parse(await roots.Content.ReadAsStringAsync());
      JsonElement listing = response.RootElement;
      Assert.Equal(JsonValueKind.Null, listing.GetProperty("parentPath").ValueKind);
      if (OperatingSystem.IsWindows()) {
        Assert.Equal(JsonValueKind.Null, listing.GetProperty("path").ValueKind);
        string systemDrive = Path.GetPathRoot(database.Path)!;
        Assert.Contains(listing.GetProperty("directories").EnumerateArray(), directory =>
            string.Equals(directory.GetProperty("path").GetString(), systemDrive,
                StringComparison.OrdinalIgnoreCase));
      } else {
        Assert.Equal(Path.GetPathRoot(Environment.CurrentDirectory), listing.GetProperty("path").GetString());
      }
    }

    string normalizedRequest = Path.Combine(parent, ".", "Alpha");
    using (HttpResponseMessage folders = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/files/directories?path={Uri.EscapeDataString(parent)}")) {
      Assert.Equal(HttpStatusCode.OK, folders.StatusCode);
      AssertPrivateResponse(folders);
      using JsonDocument response = JsonDocument.Parse(await folders.Content.ReadAsStringAsync());
      JsonElement listing = response.RootElement;
      Assert.Equal(Path.GetFullPath(parent), listing.GetProperty("path").GetString());
      Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(parent)), listing.GetProperty("parentPath").GetString());
      Assert.Equal(new[] { "Alpha", "beta" }, listing.GetProperty("directories").EnumerateArray()
          .Select(directory => directory.GetProperty("name").GetString()).ToArray());
      Assert.False(listing.GetProperty("truncated").GetBoolean());
    }

    using (HttpResponseMessage filtered = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/files/directories?path={Uri.EscapeDataString(parent)}&filter=ALP")) {
      Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
      using JsonDocument response = JsonDocument.Parse(await filtered.Content.ReadAsStringAsync());
      JsonElement listing = response.RootElement;
      JsonElement[] directories = listing.GetProperty("directories").EnumerateArray().ToArray();
      Assert.Single(directories);
      Assert.Equal("Alpha", directories[0].GetProperty("name").GetString());
    }

    using (HttpResponseMessage child = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/files/directories?path={Uri.EscapeDataString(normalizedRequest)}")) {
      Assert.Equal(HttpStatusCode.OK, child.StatusCode);
      using JsonDocument response = JsonDocument.Parse(await child.Content.ReadAsStringAsync());
      Assert.Equal(Path.GetFullPath(normalizedRequest), response.RootElement.GetProperty("path").GetString());
      Assert.Equal(Path.GetFullPath(parent), response.RootElement.GetProperty("parentPath").GetString());
      Assert.Equal("nested", response.RootElement.GetProperty("directories")[0].GetProperty("name").GetString());
    }

    using (HttpResponseMessage invalid = await RequestAsync(server.Client, HttpMethod.Get,
        "/api/files/directories?path=relative")) {
      Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
      AssertPrivateResponse(invalid);
    }
    using (HttpResponseMessage missing = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/files/directories?path={Uri.EscapeDataString(Path.Combine(ownedDirectory, "missing"))}")) {
      Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
      AssertPrivateResponse(missing);
    }
    using (HttpResponseMessage body = await RequestAsync(server.Client, HttpMethod.Get,
        "/api/files/directories", JsonBody("{}")))
      Assert.Equal(HttpStatusCode.BadRequest, body.StatusCode);
  }
}
