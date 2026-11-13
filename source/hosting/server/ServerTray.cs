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
  private readonly PopupMenu menu;

  private ServerTray(string dataRoot, string address, Action requestStop) {
    using Stream iconStream = typeof(ServerTray).Assembly.GetManifestResourceStream("Urd.Server.urd.ico")
        ?? throw new InvalidOperationException("The tray icon resource is missing.");
    icon = new Icon(iconStream, IconUtilities.GetRequiredCustomIconSize(largeIcon: false));
    // One server owns a data root, so the root gives each icon a stable identity across restarts and ports.
    trayIcon = new TrayIcon($"Urd {dataRoot}") { Icon = icon.Handle, ToolTip = $"Urd\n{address}" };
    var stop = new PopupMenuItem { Text = "Stop Urd" };
    stop.Click += (_, _) => {
      stop.Text = "Stopping…";
      stop.Enabled = false;
      requestStop();
    };
    menu = new PopupMenu { Items = { new PopupMenuItem($"Open {address}", (_, _) => Open(address)), new PopupMenuSeparator(), stop } };
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
  public static IDisposable Show(string dataRoot, Uri origin, Action requestStop) {
    var tray = new ServerTray(dataRoot, origin.GetLeftPart(UriPartial.Authority), requestStop);
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
      WindowUtilities.SetForegroundWindow(trayIcon.WindowHandle);
      menu.Show(trayIcon.WindowHandle, point.X, point.Y);
    } catch (Exception failure) {
      Console.Error.WriteLine($"Tray menu failed: {failure.Message}");
    }
  }

  private static void Open(string address) =>
      Process.Start(new ProcessStartInfo(address) { UseShellExecute = true })?.Dispose();
}
