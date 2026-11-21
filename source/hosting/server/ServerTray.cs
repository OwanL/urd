using System.Diagnostics;
using System.Drawing;
using System.Runtime.Versioning;
using H.NotifyIcon.Core;
using H.NotifyIcon.Interop;
using Microsoft.Win32;

namespace Urd.Server;

// Windows notification-area icon for one running server: shows its address, opens it and requests Stop.
// The icon and its menu live on a background message-loop thread until the process exits. Tray failures are
// reported and ignored; a missing taskbar must never stop the server.
[SupportedOSPlatform("windows6.1")]
internal sealed class ServerTray : IDisposable {
  private readonly Icon icon;
  private readonly TrayIcon trayIcon;
  private readonly PopupMenu menu = new();
  private readonly LanHosting lanHosting;
  private readonly PopupMenuItem openItem;
  private readonly PopupMenuItem lanHostingItem = new() { Text = "LAN hosting" };
  private readonly PopupMenuItem stopItem;
  private readonly List<PopupMenuItem> lanOriginItems = new();
  private volatile bool stopRequested;
  private volatile bool lanChangePending;
  private volatile bool lanTargetEnabled;
  private volatile string? lanFailure;

  private ServerTray(string dataRoot, Uri origin, LanHosting lanHosting, Action requestStop) {
    this.lanHosting = lanHosting;
    string address = origin.GetLeftPart(UriPartial.Authority);
    using Stream iconStream = typeof(ServerTray).Assembly.GetManifestResourceStream("Urd.Server.urd.ico")
        ?? throw new InvalidOperationException("The tray icon resource is missing.");
    icon = new Icon(iconStream, IconUtilities.GetRequiredCustomIconSize(largeIcon: false));
    // Explorer binds an icon GUID to the executable that registered it, even after that process exits.
    string host = Environment.ProcessPath ?? AppContext.BaseDirectory;
    string name = $"Urd {host}|{Path.TrimEndingDirectorySeparator(dataRoot)}".ToUpperInvariant();
    trayIcon = new TrayIcon(name) { Icon = icon.Handle, ToolTip = $"Urd\n{address}" };
    openItem = new PopupMenuItem($"Open {address}", (_, _) => Open(address));
    lanHostingItem.Click += (_, _) => _ = ToggleLanHosting();
    stopItem = new PopupMenuItem { Text = "Stop Urd" };
    stopItem.Click += (_, _) => {
      stopRequested = true;
      stopItem.Text = "Stopping…";
      stopItem.Enabled = false;
      lanHostingItem.Enabled = false;
      foreach (PopupMenuItem item in lanOriginItems)
        item.Enabled = false;
      requestStop();
    };
    trayIcon.MessageWindow.MouseEventReceived += (_, args) => {
      if (args.MouseEvent is MouseEvent.IconLeftMouseUp or MouseEvent.IconRightMouseUp)
        ShowMenu(args.Point);
    };
    trayIcon.MessageWindow.KeyboardEventReceived += (_, args) => {
      if (args.KeyboardEvent == KeyboardEvent.ContextMenu)
        ShowMenu(args.Point);
    };
    trayIcon.MessageWindow.TaskbarCreated += (_, _) => {
      trayIcon.TryRemove();
      TryCreate();
    };
  }

  // Disposing the result removes the icon.
  public static IDisposable Show(string dataRoot, Uri origin, LanHosting lanHosting, Action requestStop) {
    var tray = new ServerTray(dataRoot, origin, lanHosting, requestStop);
    new Thread(tray.Run) { IsBackground = true, Name = "Urd tray" }.Start();
    return tray;
  }

  public void Dispose() {
    trayIcon.Dispose();
    icon.Dispose();
  }

  private void Run() {
    if (!TryCreate())
      return;
    TryPromote();
    WindowUtilities.RunMessageLoop();
  }

  private bool TryCreate() {
    try {
      trayIcon.Create();
      return true;
    } catch (Exception failure) {
      Console.Error.WriteLine($"Tray icon unavailable: {failure.Message}");
      return false;
    }
  }

  // Windows 11 starts new icons in the hidden overflow. Shortly after the icon is added, the shell records its
  // setting by icon GUID; show ours unless the user has since hidden it there (IsPromoted = 0).
  private void TryPromote() {
    string iconGuid = trayIcon.Id.ToString("B");
    try {
      for (int attempt = 0; attempt < 20; attempt++) {
        using RegistryKey? settings = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
        if (settings is null || Promote(settings, iconGuid))
          return;
        Thread.Sleep(100);
      }
    } catch (Exception failure) {
      Console.Error.WriteLine($"Could not show the tray icon outside the overflow: {failure.Message}");
    }
  }

  private static bool Promote(RegistryKey settings, string iconGuid) {
    string? entryName = settings.GetSubKeyNames().FirstOrDefault(name => {
      using RegistryKey? candidate = settings.OpenSubKey(name);
      return string.Equals(candidate?.GetValue("IconGuid") as string, iconGuid, StringComparison.OrdinalIgnoreCase);
    });
    if (entryName is null)
      return false;
    using RegistryKey? entry = settings.OpenSubKey(entryName, writable: true);
    if (entry is not null && entry.GetValue("IsPromoted") is null)
      entry.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
    return true;
  }

  // Menu clicks run inside Show, so this also keeps a failed Open or Stop from escaping the tray thread.
  private void ShowMenu(Point point) {
    try {
      RefreshMenu();
      WindowUtilities.SetForegroundWindow(trayIcon.WindowHandle);
      menu.Show(trayIcon.WindowHandle, point.X, point.Y);
    } catch (Exception failure) {
      Console.Error.WriteLine($"Tray menu failed: {failure.Message}");
    }
  }

  private void RefreshMenu() {
    bool enabled = lanHosting.Enabled;
    IReadOnlyList<string> origins = lanHosting.Origins;
    lanHostingItem.Checked = enabled;
    lanHostingItem.Enabled = !stopRequested && !lanChangePending;
    lanHostingItem.Text = "LAN hosting";
    if (lanChangePending)
      lanHostingItem.Text = lanTargetEnabled ? "Enabling LAN hosting…" : "Disabling LAN hosting…";

    lanOriginItems.Clear();
    menu.Items.Clear();
    menu.Items.Add(openItem);
    menu.Items.Add(new PopupMenuSeparator());
    menu.Items.Add(lanHostingItem);
    if (enabled) {
      foreach (string origin in origins) {
        var item = new PopupMenuItem($"Open {origin}", (_, _) => Open(origin)) { Enabled = !stopRequested };
        lanOriginItems.Add(item);
        menu.Items.Add(item);
      }
    }
    if (lanFailure is not null)
      menu.Items.Add(new PopupMenuItem { Text = lanFailure, Enabled = false });
    menu.Items.Add(new PopupMenuSeparator());
    menu.Items.Add(stopItem);
  }

  private async Task ToggleLanHosting() {
    if (stopRequested || lanChangePending)
      return;

    try {
      bool enable = !lanHosting.Enabled;
      lanTargetEnabled = enable;
      lanChangePending = true;
      lanFailure = null;
      lanHostingItem.Enabled = false;
      lanHostingItem.Text = enable ? "Enabling LAN hosting…" : "Disabling LAN hosting…";
      await lanHosting.SetEnabledAsync(enable);
    } catch (Exception failure) {
      ReportLanFailure(failure);
    } finally {
      lanChangePending = false;
      lanHostingItem.Text = "LAN hosting";
      if (stopRequested) {
        lanHostingItem.Enabled = false;
      } else {
        lanHostingItem.Enabled = true;
        try {
          lanHostingItem.Checked = lanHosting.Enabled;
        } catch (Exception failure) {
          ReportLanFailure(failure);
        }
      }
    }
  }

  private void ReportLanFailure(Exception failure) {
    lanFailure = $"LAN hosting failed: {failure.Message}";
    try {
      Console.Error.WriteLine($"LAN hosting failed: {failure.Message}");
    } catch {
      // A closed launch log must not escape the tray callback.
    }
  }

  private static void Open(string address) =>
      Process.Start(new ProcessStartInfo(address) { UseShellExecute = true })?.Dispose();
}
