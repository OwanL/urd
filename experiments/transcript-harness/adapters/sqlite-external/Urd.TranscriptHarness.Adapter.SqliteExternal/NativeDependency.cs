using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Urd.TranscriptHarness.Adapter.SqliteExternal;

// The shared host loads this adapter by path inside an AssemblyLoadContext
// whose resolver reads this adapter's .deps.json; that also covers the SQLite
// SQLitePCLRaw managed dependencies and the native e_sqlite3. What remains is
// the explicit battery wiring: SQLitePCL.raw must be pointed at the e_sqlite3
// provider before any Microsoft.Data.Sqlite call in this assembly (the
// bundle's module initializer does not reliably fire in a LoadFrom-hosted
// plugin). Blocks/rows-proven pattern, minimal; no plugin machinery beyond
// this is added.

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

            SetProvider();

            _resolved = true;
        }
    }

    // Separate method with NoInlining: the SQLitePCL.Batteries_V2 assembly
    // reference must not be resolved while JIT-compiling/before running the
    // hook registrations above.
    [global::System.Runtime.CompilerServices.MethodImpl(
        global::System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void SetProvider() => SQLitePCL.Batteries_V2.Init();
}