using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Urd.Core.Tools;

// Identifies an already-opened file rather than trusting a mutable pathname.
internal static class OpenedFilePath {
  internal static string? Resolve(SafeFileHandle handle) {
    if (OperatingSystem.IsWindows()) {
      var buffer = new char[512];
      uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
      if (length >= buffer.Length && length <= 32768) {
        buffer = new char[length];
        length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
      }
      if (length == 0 || length >= buffer.Length)
        return null;
      string path = new(buffer, 0, (int)length);
      if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        return @"\\" + path[8..];
      return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : null;
    }
    if (OperatingSystem.IsLinux()) {
      // procfs exposes the kernel's path for this descriptor. The calling FileStream owns
      // the handle for this entire synchronous lookup; only its numeric identifier is needed.
#pragma warning disable S3869
      string descriptor = $"/proc/self/fd/{handle.DangerousGetHandle()}";
#pragma warning restore S3869
      return new FileInfo(descriptor).LinkTarget;
    }
    if (OperatingSystem.IsMacOS()) {
      var buffer = new byte[1024];
      if (fcntl(handle, 50, buffer) == -1)
        return null;
      int length = Array.IndexOf(buffer, (byte)0);
      return length < 0 ? null : Encoding.UTF8.GetString(buffer, 0, length);
    }
    return null;
  }

  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, [Out] char[] path, uint length, uint flags);

  [DllImport("libc", SetLastError = true)]
  private static extern int fcntl(SafeFileHandle handle, int command, byte[] path);
}
