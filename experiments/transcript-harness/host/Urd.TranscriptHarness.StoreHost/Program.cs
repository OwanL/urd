using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Urd.TranscriptHarness.Contracts;
using Urd.TranscriptHarness.Core;
using Urd.TranscriptHarness.ReferenceStore;

namespace Urd.TranscriptHarness.StoreHost;

// Store host child process. The default store is the in-process reference
// adapter; real adapters are loaded by explicit assembly path plus type name
// via AdapterPluginLoader (AssemblyDependencyResolver-based), so adding an
// adapter never requires editing shared host files.

public static class Program
{
    public static int Main(string[] args)
    {
        var storeTypeArg = Arg(args, "--store-type") ?? "ReferenceTranscriptStore";
        var assemblyPath = Arg(args, "--store-assembly");

        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var stdout = new StreamWriter(
            Console.OpenStandardOutput(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };

        try
        {
            var store = CreateStore(storeTypeArg, assemblyPath);
            new HostServer(store, store as IStoreStatistics, Console.In, stdout).Serve();
            return 0;
        }
        catch (Exception ex)
        {
            stdout.WriteLine($"{{\"ev\":\"fatal\",\"p\":{{\"message\":{Json.Escape(ex.Message)}}}}}");
            return 1;
        }
    }

    private static ITranscriptStore CreateStore(string storeType, string? assemblyPath)
    {
        if (assemblyPath is null)
        {
            var referenceAssembly = typeof(ReferenceTranscriptStore).Assembly;
            var type = referenceAssembly.GetType(storeType) /* full name */
                ?? referenceAssembly.GetType($"Urd.TranscriptHarness.ReferenceStore.{storeType}") /* short name */
                ?? throw new HarnessError($"Unknown built-in store type '{storeType}'");
            return Activator.CreateInstance(type) as ITranscriptStore
                ?? throw new HarnessError($"Type '{type.FullName}' does not implement ITranscriptStore");
        }
        var loaded = AdapterPluginLoader.Load(assemblyPath, storeType);
        return loaded;
    }

    private static string? Arg(string[] args, string name)
    {
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == name) return args[i + 1];
        }
        return null;
    }
}

internal static class Json
{
    public static string Escape(string text) => System.Text.Json.JsonSerializer.Serialize(text);
}

/// <summary>
/// Loads one adapter plugin by explicit assembly path plus type name. A plugin
/// directory carries its own dependencies package assets; an
/// AssemblyDependencyResolver (plugin .deps.json) resolves package and native
/// assets. Shared contract assembly types intentionally fall back to the
/// default context so ITranscriptStore/records bind to the SAME loaded
/// instance the host runs on (cross-context type identity is otherwise
/// broken). This is an explicit-path loader for known adapters, not a plugin
/// framework: no discovery, no isolation from host code, no sandboxing.
/// </summary>
public static class AdapterPluginLoader
{
    public static ITranscriptStore Load(string assemblyPath, string typeName)
    {
        var path = Path.GetFullPath(assemblyPath);
        if (!File.Exists(path)) throw new HarnessError($"Adapter assembly not found: {path}");
        var context = new AdapterLoadContext(path);
        Assembly assembly;
        try
        {
            assembly = context.LoadFromAssemblyPath(path);
        }
        catch (Exception ex)
        {
            throw new HarnessError($"Failed to load adapter assembly '{path}': {ex.Message}");
        }
        var type = assembly.GetType(typeName, throwOnError: false)
            ?? throw new HarnessError($"Type '{typeName}' not found in '{path}'");
        return Activator.CreateInstance(type) as ITranscriptStore
            ?? throw new HarnessError($"Type '{type.FullName}' does not implement ITranscriptStore");
    }
}

internal sealed class AdapterLoadContext(string mainAssemblyPath)
    : AssemblyLoadContext($"adapter:{Path.GetFileNameWithoutExtension(mainAssemblyPath)}", isCollectible: false)
{
    private readonly AssemblyDependencyResolver _resolver = new(mainAssemblyPath);

    protected override Assembly? Load(AssemblyName name)
    {
        // Contracts (and anything the host already binds to) must not load a
        // second copy here: null falls back to the default context, which has
        // the shared instance. Package dependencies resolve via .deps.json.
        if (name.Name == "Urd.TranscriptHarness.Contracts")
        {
            return null;
        }
        var path = _resolver.ResolveAssemblyToPath(name);
        return path is not null ? LoadFromAssemblyPath(path) : null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is not null ? LoadUnmanagedDllFromPath(path) : base.LoadUnmanagedDll(unmanagedDllName);
    }
}