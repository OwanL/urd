using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Playwright;
using Urd.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace Urd.Backend.Tests.Tools;

public sealed class WebBrowserLiveTests(ITestOutputHelper output) {
  [Fact]
  [Trait("Category", "LiveWeb")]
  public async Task WindowsColdWarmCancellationAndPublicOnlyEgress() {
    if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("URD_TEST_LIVE_WEB") != "1")
      return;

    DirectoryInfo cache = Directory.CreateTempSubdirectory("urd-browser-test-");
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
    using var sampling = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
    Task<long> peak = SampleMemoryAsync(cache.FullName, sampling.Token);
    var loopback = new TcpListener(IPAddress.Loopback, 0);
    loopback.Start();
    try {
      var time = Stopwatch.StartNew();
      var progress = new Progress<string>(message => output.WriteLine($"{time.Elapsed}: {message}"));
      await using (var browser = new WebBrowser(cache.FullName, TimeSpan.FromSeconds(2), progress)) {
        Assert.Empty(ChromiumProcesses(cache.FullName));
        Uri example = new UriBuilder(Uri.UriSchemeHttps, "example.com").Uri;
        await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => browser.WithPageAsync(async (page, _) => {
          Assert.Equal(200, (await NavigateAsync(page, example)).Status);
          Assert.Equal("Example Domain", await page.TitleAsync());
          return true;
        }, timeout.Token)));
        int[] coldProcesses = ChromiumProcesses(cache.FullName);
        output.WriteLine($"Concurrent cold calls: {time.Elapsed}; Chromium processes: {coldProcesses.Length}");
        Assert.NotEmpty(coldProcesses);
        Assert.Single(cache.EnumerateDirectories());

        time.Restart();
        Uri local = new UriBuilder(Uri.UriSchemeHttp, IPAddress.Loopback.ToString(), ((IPEndPoint)loopback.LocalEndpoint).Port).Uri;
        Uri redirect = new UriBuilder(Uri.UriSchemeHttps, "fixture.example") { Path = "/redirect" }.Uri;
        Uri subresource = new UriBuilder(local) { Path = "/image" }.Uri;
        await browser.WithPageAsync(async (page, _) => {
          await AssertBlockedAsync(page, local);
          await page.RouteAsync(redirect.AbsoluteUri, route => route.FulfillAsync(new RouteFulfillOptions {
            Status = 302,
            Headers = new Dictionary<string, string> { ["Location"] = local.AbsoluteUri }
          }));
          await AssertBlockedAsync(page, redirect);
          Assert.Equal(local.AbsoluteUri, page.Url);
          await page.RouteAsync(example.AbsoluteUri, route => route.FulfillAsync(new RouteFulfillOptions {
            ContentType = "text/html",
            Body = $"<img src=\"{subresource.AbsoluteUri}\">"
          }));
          Task<IResponse> blockedSubresource = page.WaitForResponseAsync(response => response.Url == subresource.AbsoluteUri);
          Assert.Equal(200, (await NavigateAsync(page, example)).Status);
          Assert.Equal(451, (await blockedSubresource.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token)).Status);
          return true;
        }, timeout.Token);
        Assert.False(loopback.Pending());
        Assert.NotEmpty(ChromiumProcesses(cache.FullName).Intersect(coldProcesses));
        output.WriteLine($"Warm direct/redirect/subresource: {time.Elapsed}");

        string file = Path.Combine(cache.FullName, "local.txt");
        await File.WriteAllTextAsync(file, "Local fixture", timeout.Token);
        await browser.WithPageAsync(async (page, _) => {
          await Assert.ThrowsAsync<PlaywrightException>(() => NavigateAsync(page, new Uri(file)));
          return true;
        }, timeout.Token);

        var entered = new TaskCompletionSource<IPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        Task<bool> waiting = browser.WithPageAsync(async (page, cancellation) => {
          entered.SetResult(page);
          await Task.Delay(Timeout.Infinite, cancellation);
          return false;
        }, operation.Token);
        IPage cancelledPage = await entered.Task.WaitAsync(timeout.Token);
        var siblingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkSibling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> sibling = browser.WithPageAsync(async (page, cancellation) => {
          siblingEntered.SetResult();
          await checkSibling.Task.WaitAsync(cancellation);
          return (await NavigateAsync(page, example)).Status;
        }, timeout.Token);
        await siblingEntered.Task.WaitAsync(timeout.Token);
        await operation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.True(cancelledPage.IsClosed);
        checkSibling.SetResult();
        Assert.Equal(200, await sibling);
        await WaitForExitAsync(cache.FullName, timeout.Token);
        output.WriteLine("Idle shutdown: no Chromium or driver processes survived.");
        await browser.WithPageAsync(async (page, _) => (await NavigateAsync(page, example)).Status, timeout.Token);
        Assert.NotEmpty(ChromiumProcesses(cache.FullName));
      }
      await WaitForExitAsync(cache.FullName, timeout.Token);
      output.WriteLine("Explicit disposal: no Chromium or driver processes survived.");
    } finally {
      loopback.Stop();
      await sampling.CancelAsync();
      output.WriteLine($"Peak Chromium/driver working set: {(await peak) / (1024 * 1024)} MiB");
      cache.Delete(recursive: true);
    }
  }

  [Fact]
  [Trait("Category", "LiveWeb")]
  public async Task CancellingColdInstallDoesNotPublishPartialBrowser() {
    if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("URD_TEST_LIVE_WEB") != "1")
      return;

    DirectoryInfo cache = Directory.CreateTempSubdirectory("urd-browser-cancel-test-");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    using var cancel = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
    var downloading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    try {
      var progress = new Progress<string>(_ => downloading.TrySetResult());
      await using (var browser = new WebBrowser(cache.FullName, progress: progress)) {
        Task<bool> waiting = browser.WithPageAsync((_, _) => Task.FromResult(true), cancel.Token);
        await downloading.Task.WaitAsync(timeout.Token);
        await Task.Delay(250, timeout.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
      }
      Assert.Empty(cache.EnumerateFileSystemInfos());
      await WaitForExitAsync(cache.FullName, timeout.Token);
    } finally {
      cache.Delete(recursive: true);
    }
  }

  private static async Task<IResponse> NavigateAsync(IPage page, Uri address) =>
      (await page.GotoAsync(address.AbsoluteUri, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded }))!;

  private static async Task AssertBlockedAsync(IPage page, Uri address) {
    IResponse blocked = await NavigateAsync(page, address);
    Assert.Equal(451, blocked.Status);
    Assert.Equal("destination", await blocked.HeaderValueAsync("x-urd-web-blocked"));
  }

  private static int[] ChromiumProcesses(string cache) {
    Process[] processes = Process.GetProcessesByName("chrome");
    try {
      return processes.Where(process => IsManaged(process, cache)).Select(process => process.Id).ToArray();
    } finally {
      Array.ForEach(processes, process => process.Dispose());
    }
  }

  private static bool IsManaged(Process process, string cache) {
    try {
      return process.MainModule?.FileName.StartsWith(cache + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) == true;
    } catch (Exception failure) when (failure is InvalidOperationException or System.ComponentModel.Win32Exception) {
      return false;
    }
  }

  private static string DriverDirectory => Path.Combine(AppContext.BaseDirectory, ".playwright", "node");

  private static async Task<long> SampleMemoryAsync(string cache, CancellationToken cancellation) {
    long peak = 0;
    try {
      while (true) {
        peak = Math.Max(peak, WorkingSet("chrome", cache) + WorkingSet("node", DriverDirectory));
        await Task.Delay(100, cancellation);
      }
    } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
      return peak;
    }
  }

  private static long WorkingSet(string executable, string directory) {
    Process[] processes = Process.GetProcessesByName(executable);
    try {
      long bytes = 0;
      foreach (Process process in processes.Where(process => IsManaged(process, directory))) {
        try {
          bytes += process.WorkingSet64;
        } catch (InvalidOperationException) {
          // The process exited during sampling.
        }
      }
      return bytes;
    } finally {
      Array.ForEach(processes, process => process.Dispose());
    }
  }

  private static async Task WaitForExitAsync(string cache, CancellationToken cancellation) {
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
    deadline.CancelAfter(TimeSpan.FromSeconds(12));
    while (ChromiumProcesses(cache).Length > 0 || WorkingSet("node", DriverDirectory) > 0)
      await Task.Delay(100, deadline.Token);
  }
}
