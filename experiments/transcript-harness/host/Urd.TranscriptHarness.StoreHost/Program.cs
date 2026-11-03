using System.Reflection;
using System.Text;
using Urd.TranscriptHarness.Contracts;
using Urd.TranscriptHarness.Core;
using Urd.TranscriptHarness.ReferenceStore;

namespace Urd.TranscriptHarness.StoreHost;

// Store host child process. The default store is the in-process reference
// adapter; real adapters are loaded by explicit assembly path plus type name,
// so adding an adapter never requires editing shared host files.

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
        Type type;
        if (assemblyPath is not null)
        {
            if (assemblyPath.Length == 0) throw new HarnessError("Empty --store-assembly path");
            var assembly = Assembly.LoadFrom(assemblyPath);
            type = assembly.GetType(storeType)
                ?? throw new HarnessError($"Type '{storeType}' not found in '{assemblyPath}'");
        }
        else
        {
            var referenceAssembly = typeof(ReferenceTranscriptStore).Assembly;
            type = referenceAssembly.GetType(storeType) /* full name */
                ?? referenceAssembly.GetType($"Urd.TranscriptHarness.ReferenceStore.{storeType}") /* short name */
                ?? throw new HarnessError($"Unknown built-in store type '{storeType}'");
        }
        return Activator.CreateInstance(type) as ITranscriptStore
            ?? throw new HarnessError($"Type '{type.FullName}' does not implement ITranscriptStore");
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