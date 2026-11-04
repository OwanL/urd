using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Urd.TranscriptHarness.Adapter.SqliteBlocks;

// The store host loads this adapter by path inside an AssemblyLoadContext whose
// resolver reads THIS adapter's .deps.json — that covers managed assembly
// resolution (Microsoft.Data.Sqlite, SQLitePCLRaw.*) and native-library
// resolution for the provider's P/Invoke. What it does NOT cover is the
// SQLitePCLRaw battery wiring: SQLitePCL.raw must be pointed at the
// e_sqlite3 provider explicitly (the bundle's module initializer does not
// reliably fire in a LoadFrom-hosted plugin). This adapter-owned type is the
// minimal, idempotent battery init required by Microsoft.Data.Sqlite; it adds
// no plugin machinery of its own. The hook registrations below mirror the
// rows adapter's proven pattern and only affect provider loading for this
// adapter's own dependency set.

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

            // Set the e_sqlite3 provider explicitly (the minimal explicit
            // Batteries init, per the adapter-owner coordination note).
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