using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using Sts2Headless;

namespace SpireCoach;

internal static class SearchHost
{
    public static int Main(string[] args)
    {
        if (args.Length != 2) return 2;
        var runtime = Path.Combine(args[0], "runtime");
        AssemblyLoadContext.Default.Resolving += (context, name) => {
            string path = Path.Combine(runtime, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
        SetNativeRuntime(runtime);
        GameAssemblies.Register();
        return Serve(args[0], args[1]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SetNativeRuntime(string runtime)
    {
        NativeLibrary.SetDllImportResolver(typeof(Microsoft.ML.OnnxRuntime.InferenceSession).Assembly,
            (name, _, _) => name.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase)
                ? NativeLibrary.Load(Path.Combine(runtime, OperatingSystem.IsWindows() ? "onnxruntime.dll" : "libonnxruntime.so")) : IntPtr.Zero);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Serve(string dir, string vocab)
    {
        var output = Console.Out;
        Console.SetOut(Console.Error);
        void Send(SearchReply reply) { output.WriteLine(SearchLimits.ReplyPrefix + JsonSerializer.Serialize(reply)); output.Flush(); }
        AppContext.SetData("Sts2NoDownsideRelics", Path.Combine(dir, "runtime", "no_downside_relics.txt"));
        Bootstrap.Init();
        Vocab.Load(vocab);
        Observation.RouteSummary = true;
        var commands = new BlockingCollection<SearchCommand>();
        int cancelledThrough = 0;
        _ = Task.Run(() => {
            try {
                while (Console.ReadLine() is { } line) {
                    var cmd = JsonSerializer.Deserialize<SearchCommand>(line)!;
                    if (cmd.Op == "cancel") Interlocked.Exchange(ref cancelledThrough, cmd.Id);
                    else commands.Add(cmd);
                }
            } finally { Environment.Exit(0); }
        });
        using var engine = new CombatSearch(dir,vocab);
        Send(new(0, "online"));
        int prepared = 0;
        foreach (var cmd in commands.GetConsumingEnumerable())
        {
            bool Cancelled() => Volatile.Read(ref cancelledThrough) >= cmd.Id;
            try {
                if (Cancelled()) { Send(new(cmd.Id, "cancelled")); continue; }
                if (cmd.Op == "prepare") {
                    prepared = 0;
                    if (cmd.Position != null && engine.Prepare(cmd.Position, Cancelled)) prepared = cmd.Id;
                    Send(new(cmd.Id, prepared == cmd.Id ? "ready" : "unmatched"));
                } else if (cmd.Op == "search" && prepared == cmd.Id) {
                    prepared = 0;
                    int budget = Math.Clamp(cmd.BudgetMs,1,30000);
                    Send(engine.Search(cmd.Id, budget, Cancelled));
                } else Send(new(cmd.Id, "unmatched"));
            } catch (Exception ex) {
                prepared = 0;
                Console.Error.WriteLine(ex.GetBaseException().Message);
                Send(new(cmd.Id, "unavailable"));
            }
        }
        return 0;
    }
}
