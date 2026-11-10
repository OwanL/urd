using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Urd.Core.Tools;

// The Windows boundary: suspended launch, explicit inherited handles and job accounting.
[SupportedOSPlatform("windows")]
internal static class WindowsShell {
  private const uint KillOnClose = 0x2000;
  private const uint CreateSuspended = 0x4;
  private const uint CreateUnicodeEnvironment = 0x400;
  private const uint ExtendedStartupInfoPresent = 0x80000;
  private const uint CreateNoWindow = 0x08000000;
  private const nuint InheritedHandleList = 0x20002;
  private const int BrokenPipe = 109;

  public static SafeFileHandle CreateJob() {
    SafeFileHandle job = CreateJobObjectW(nint.Zero, null);
    if (job.IsInvalid) {
      var creationError = Error("Create job");
      job.Dispose();
      throw creationError;
    }
    var limits = new JobLimits();
    limits.Basic.LimitFlags = KillOnClose;
    if (SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<JobLimits>()))
      return job;
    var error = Error("Set kill-on-close job limit");
    job.Dispose();
    throw error;
  }

  public static (SafeFileHandle Process, SafeFileHandle Output) Start(
      string executable, string command, string directory, IReadOnlyDictionary<string, string> environment,
      SafeFileHandle chatJob, SafeFileHandle callJob) {
    var security = new SecurityAttributes { Size = Marshal.SizeOf<SecurityAttributes>(), Inherit = true };
    if (!CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref security, 4096)) {
      var error = Error("Create output pipe");
      read.Dispose();
      write.Dispose();
      throw error;
    }
    using SafeFileHandle outputWriter = write;
    try {
      Check(SetHandleInformation(read, 1, 0), "Prevent output reader inheritance");
      using SafeFileHandle input = CreateFileW("NUL", 0x80000000, 3, ref security, 3, 0, nint.Zero);
      if (input.IsInvalid)
        throw Error("Open shell input");
      var started = StartSuspended(executable, command, directory, environment, input, outputWriter);
      using SafeFileHandle thread = started.Thread;
      AssignAndResume(started.Process, thread, chatJob, callJob);
      return (started.Process, read);
    } catch {
      read.Dispose();
      throw;
    }
  }

  private static void AssignAndResume(SafeFileHandle process, SafeFileHandle thread,
      SafeFileHandle chatJob, SafeFileHandle callJob) {
    try {
      Check(AssignProcessToJobObject(chatJob, process), "Assign chat job");
      Check(AssignProcessToJobObject(callJob, process), "Assign call job");
      if (ResumeThread(thread) == uint.MaxValue)
        throw Error("Resume owned shell");
    } catch (Exception failure) {
      try {
        Check(TerminateProcess(process, 1), "Terminate failed shell launch");
        if (WaitForSingleObject(process, 10000) != 0)
          throw new TimeoutException("Failed shell launch did not terminate within 10 seconds.");
      } catch (Exception cleanup) {
        throw new AggregateException(failure, cleanup);
      } finally {
        process.Dispose();
      }
      throw;
    }
  }

#pragma warning disable S3869 // STARTUPINFOEX requires raw handles; caller retains their using scopes through synchronous launch.
  private static (SafeFileHandle Process, SafeFileHandle Thread) StartSuspended(string executable, string command, string directory,
      IReadOnlyDictionary<string, string> environment, SafeFileHandle input, SafeFileHandle output) {
    nint attributes = nint.Zero;
    nuint size = 0;
    InitializeProcThreadAttributeList(nint.Zero, 1, 0, ref size);
    string environmentBlock = EnvironmentBlock(environment);
    nint inherited = nint.Zero;
    nint block = nint.Zero;
    try {
      inherited = Marshal.AllocHGlobal(2 * nint.Size);
      block = Marshal.StringToHGlobalUni(environmentBlock);
      attributes = Marshal.AllocHGlobal(checked((int)size));
      Check(InitializeProcThreadAttributeList(attributes, 1, 0, ref size), "Initialize handle allowlist");
      try {
        Marshal.WriteIntPtr(inherited, input.DangerousGetHandle());
        Marshal.WriteIntPtr(inherited, nint.Size, output.DangerousGetHandle());
        Check(UpdateProcThreadAttribute(attributes, 0, InheritedHandleList, inherited,
            (nuint)(2 * nint.Size), nint.Zero, nint.Zero), "Set inherited handle allowlist");
        var startup = new StartupInfoEx {
          Startup = new StartupInfo {
            Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
            Input = input.DangerousGetHandle(), Output = output.DangerousGetHandle(), ErrorOutput = output.DangerousGetHandle()
          },
          Attributes = attributes
        };
        char[] arguments = $"{Quote(executable)} -c {Quote(command)}\0".ToCharArray();
        Check(CreateProcessW(executable, arguments, nint.Zero, nint.Zero, true,
            CreateSuspended | CreateUnicodeEnvironment | ExtendedStartupInfoPresent | CreateNoWindow,
            block, directory, ref startup, out ProcessInformation started),
            "Create suspended Git Bash");
        return (new SafeFileHandle(started.Process, true), new SafeFileHandle(started.Thread, true));
      } finally {
        DeleteProcThreadAttributeList(attributes);
      }
    } finally {
      Marshal.FreeHGlobal(attributes);
      Marshal.FreeHGlobal(inherited);
      Marshal.FreeHGlobal(block);
    }
  }

#pragma warning restore S3869

  private static string EnvironmentBlock(IReadOnlyDictionary<string, string> environment) {
    var entries = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach ((string key, string value) in environment) {
      if (string.IsNullOrEmpty(key) || key.Contains('=') || key.Contains('\0') || value is null || value.Contains('\0'))
        throw new ArgumentException("Shell environment requires nonempty names without '=' or NUL, and values without NUL.", nameof(environment));
      if (!entries.TryAdd(key, value))
        throw new ArgumentException("Shell environment contains duplicate Windows variable names.", nameof(environment));
    }
    return string.Join('\0', entries.Select(entry => $"{entry.Key}={entry.Value}")) + "\0\0";
  }

  // Windows argv quoting, including backslashes immediately before quotes or the closing quote.
  private static string Quote(string value) {
    var quoted = new StringBuilder("\"");
    int slashes = 0;
    foreach (char character in value) {
      if (character == '\\') {
        slashes++;
        continue;
      }
      quoted.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
      quoted.Append(character);
      slashes = 0;
    }
    return quoted.Append('\\', slashes * 2).Append('"').ToString();
  }

  public static int? ExitCode(SafeFileHandle process) {
    uint wait = WaitForSingleObject(process, 0);
    if (wait == 258)
      return null;
    if (wait != 0)
      throw Error("Wait for shell exit");
    Check(GetExitCodeProcess(process, out uint code), "Read shell exit code");
    return unchecked((int)code);
  }

  public static int AvailableOutput(SafeFileHandle output) {
    if (PeekNamedPipe(output, nint.Zero, 0, nint.Zero, out uint available, nint.Zero))
      return checked((int)available);
    int error = Marshal.GetLastPInvokeError();
    if (error == BrokenPipe)
      return 0;
    throw new Win32Exception(error, "Inspect shell output pipe");
  }

  public static void Terminate(SafeFileHandle job) => Check(TerminateJobObject(job, 1), "Terminate shell job");

  public static uint ActiveProcesses(SafeFileHandle job) {
    Check(QueryInformationJobObject(job, 1, out JobAccounting accounting,
        Marshal.SizeOf<JobAccounting>(), nint.Zero), "Read job process count");
    return accounting.ActiveProcessCount;
  }

  public static async Task WaitForEmptyAsync(SafeFileHandle job) {
    var elapsed = Stopwatch.StartNew();
    while (ActiveProcesses(job) != 0) {
      if (elapsed.Elapsed >= TimeSpan.FromSeconds(10))
        throw new TimeoutException("Windows job still contains processes after 10 seconds of cleanup.");
      await Task.Delay(20).ConfigureAwait(false);
    }
  }

  private static Win32Exception Error(string operation) => new(Marshal.GetLastPInvokeError(), operation);
  private static void Check(bool success, string operation) {
    if (!success)
      throw Error(operation);
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct SecurityAttributes {
    public int Size;
    public nint Descriptor;
    [MarshalAs(UnmanagedType.Bool)] public bool Inherit;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct BasicLimits {
    public long ProcessTime, JobTime;
    public uint LimitFlags;
    public nuint MinimumWorkingSet, MaximumWorkingSet;
    public uint ActiveProcessLimit;
    public nuint Affinity;
    public uint PriorityClass, SchedulingClass;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct JobLimits {
    public BasicLimits Basic;
    public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
    public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct JobAccounting {
    public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
    public uint PageFaults, TotalProcesses, ActiveProcessCount, TerminatedProcesses;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct StartupInfo {
    public int Size;
    public nint Reserved, Desktop, Title;
    public int X, Y, Width, Height, CharactersX, CharactersY, Fill, Flags;
    public short ShowWindow, ReservedSize;
    public nint ReservedData, Input, Output, ErrorOutput;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct StartupInfoEx {
    public StartupInfo Startup;
    public nint Attributes;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct ProcessInformation {
    public nint Process, Thread;
    public uint ProcessId, ThreadId;
  }

  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern SafeFileHandle CreateJobObjectW(nint attributes, string? name);
  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref JobLimits limits, int size);
  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool QueryInformationJobObject(SafeFileHandle job, int informationClass, out JobAccounting accounting, int size, nint returnedSize);
  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes security, int size);
  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern SafeFileHandle CreateFileW(string file, uint access, uint share, ref SecurityAttributes security, uint disposition, uint flags, nint template);
  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool InitializeProcThreadAttributeList(nint list, int count, uint flags, ref nuint size);
  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returnedSize);
  [DllImport("kernel32.dll")]
  private static extern void DeleteProcThreadAttributeList(nint list);
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool CreateProcessW(string application, [In, Out] char[] command, nint processSecurity, nint threadSecurity,
      [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, nint environment, string directory, ref StartupInfoEx startup, out ProcessInformation process);
  [DllImport("kernel32.dll", SetLastError = true)]
  private static extern uint ResumeThread(SafeFileHandle thread);
  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool TerminateProcess(SafeFileHandle process, uint exitCode);
  [DllImport("kernel32.dll", SetLastError = true)]
  private static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GetExitCodeProcess(SafeFileHandle process, out uint code);
  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool PeekNamedPipe(SafeFileHandle pipe, nint buffer, uint size, nint read, out uint available, nint remaining);
}
