using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Urd.TranscriptHarness.Adapter.SqliteRows;

// The store host loads this adapter by path (Assembly.LoadFrom) outside the
// host's deps.json, so SQLitePCLRaw's managed and native dependencies are not
// found by default probing. This adapter-owned type wires dependency resolution
// from THIS adapter's own .deps.json (AssemblyDependencyResolver):
//   - managed: Microsoft.Data.Sqlite + SQLitePCLRaw.* resolve from the adapter
//     bin directory;
//   - native: the provider assembly's P/Invoke "e_sqlite3" resolves against the
//     adapter's runtimes/ tree.
// The shared host is not modified; the host may later add its own resolver
// (owner work), which composes harmlessly. The hook is idempotent and only
// resolves what this adapter's deps.json actually lists.

internal static class NativeDependency
{
    private static bool _resolved;
    private static readonly object Gate = new();

    public static void EnsureResolved()
    {
        if (_resolved) return;
        lock (Gate)
        {
            if (_resolved) return;
            var adapterPath = typeof(NativeDependency).Assembly.Location;
            var resolver = new AssemblyDependencyResolver(adapterPath);

            AssemblyLoadContext.Default.Resolving += (_, assemblyName) =>
            {
                var path = resolver.ResolveAssemblyToPath(assemblyName);
                return path is null ? null : AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
            };

            // Native resolution must be registered on the assembly that DECLARED
            // the P/Invoke before its first call: SQLitePCLRaw.provider.e_sqlite3.
            var providerAssembly = typeof(SQLitePCL.SQLite3Provider_e_sqlite3).Assembly;
            NativeLibrary.SetDllImportResolver(providerAssembly, (libraryName, _, _) =>
            {
                var path = resolver.ResolveUnmanagedDllToPath(libraryName);
                return path is null ? 0 : NativeLibrary.Load(path);
            });

            // Set the e_sqlite3 provider explicitly: the bundle's module
            // initializer does not fire in a LoadFrom-hosted scenario because
            // the batteries module is never JIT-loaded on its own.
            SetProvider();

            _resolved = true;
        }
    }

    // Separate method with NoInlining: the SQLitePCL.Batteries_V2 assembly
    // reference must not be resolved while JIT-compiling/before running the
    // hook registrations above (the Resolving hook is not registered yet then).
    [global::System.Runtime.CompilerServices.MethodImpl(
        global::System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void SetProvider() => SQLitePCL.Batteries_V2.Init();
}