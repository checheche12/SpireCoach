using System.Runtime.Loader;
using System.Runtime.InteropServices;
using Godot;
using MegaCrit.Sts2.Core.Modding;

namespace SpireCoach;

[ModInitializer(nameof(Initialize))]
public static class Entry
{
    public static void Initialize()
    {
        try
        {
            var dir = Path.GetDirectoryName(typeof(Entry).Assembly.Location)
                ?? throw new DirectoryNotFoundException("첨탑 코치 설치 경로를 확인할 수 없습니다.");
            var runtime = Path.Combine(dir, "runtime");
            var context = AssemblyLoadContext.GetLoadContext(typeof(Entry).Assembly)!;
            context.LoadFromAssemblyPath(Path.GetFullPath(Path.Combine(runtime, "System.Numerics.Tensors.dll")));
            var ort = context.LoadFromAssemblyPath(Path.GetFullPath(Path.Combine(runtime, "Microsoft.ML.OnnxRuntime.dll")));
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new PlatformNotSupportedException("x86-64 실행 환경이 필요합니다.");
            var native = OperatingSystem.IsLinux() ? "libonnxruntime.so" : OperatingSystem.IsWindows()
                ? "onnxruntime.dll" : throw new PlatformNotSupportedException("Windows 또는 Linux가 필요합니다.");
            NativeLibrary.SetDllImportResolver(ort, (name, _, _) => name is "onnxruntime" or "onnxruntime.dll" or "libonnxruntime" or "libonnxruntime.so"
                ? NativeLibrary.Load(Path.Combine(runtime, native)) : IntPtr.Zero);
            var game = typeof(ModInitializerAttribute).Assembly;
            var branch = game.GetType("MegaCrit.Sts2.Core.Models.Cards.Optimism") != null ? "Beta114"
                : game.GetType("MegaCrit.Sts2.Core.Models.Characters.Fool") != null ? "Beta" : "Stable";
            var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(Path.Combine(runtime, $"SpireCoach.{branch}.dll")));
            var attach = assembly.GetType("SpireCoach.Coach", throwOnError: true)!
                .GetMethod("Attach")!.CreateDelegate<Action<SceneTree, string>>();
            GD.Print($"[SpireCoach] {branch} adapter");
            Callable.From(() => attach((SceneTree)Godot.Engine.GetMainLoop(), dir)).CallDeferred();
        }
        catch (Exception ex) { GD.PushError("[SpireCoach] 초기화 오류: " + ex); }
    }
}
