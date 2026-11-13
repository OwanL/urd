using System.Diagnostics;
using System.Drawing;
using System.Runtime.Versioning;
using H.NotifyIcon.Core;
using H.NotifyIcon.Interop;

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
    if (TryCreate())
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
