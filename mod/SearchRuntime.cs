using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SpireCoach;

internal static class SearchRuntime
{
    internal static string CacheRoot { get; set; } = Path.Combine(Directory.GetParent(LocalData.Folder)!.FullName, "search");
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string link, string existing, IntPtr reserved);

    internal static async Task<Process> Start(string mod, string game, string branch, Guid version, CancellationToken token)
    {
        string cache = Path.Combine(CacheRoot, branch);
        Directory.CreateDirectory(cache);
        string source = Path.Combine(mod, "runtime", "search");
        string exe = OperatingSystem.IsWindows() ? "SpireCoach.SearchHost.exe" : "SpireCoach.SearchHost";
        string managed = Path.Combine(source, branch + ".dll");
        string stamp = Path.Combine(cache, "build.txt");
        string key = version + " " + Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(managed, token)));
        using (var lease = new FileStream(Path.Combine(cache, "setup.lock"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None)) {
            if (!File.Exists(stamp) || File.ReadAllText(stamp) != key) {
                foreach (var file in Directory.EnumerateFiles(game)) {
                    token.ThrowIfCancellationRequested();
                    string name = Path.GetFileName(file);
                    if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".so") || name.Contains(".so."))
                        Install(file, Path.Combine(cache, name), link: true);
                }
                Install(Path.Combine(game,"sts2.deps.json"), Path.Combine(cache,"SpireCoach.SearchHost.deps.json"));
                Install(Path.Combine(game,"sts2.runtimeconfig.json"), Path.Combine(cache,"SpireCoach.SearchHost.runtimeconfig.json"));
                Install(managed, Path.Combine(cache,"SpireCoach.SearchHost.dll"));
                Install(Path.Combine(source,exe),Path.Combine(cache,exe));
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(cache,exe), UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
                ExtractEnglish(Path.Combine(Directory.GetParent(game)!.FullName, "SlayTheSpire2.pck"), Path.Combine(cache,"res"), token);
                File.WriteAllText(stamp,key);
            }
        }
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(Path.Combine(cache,exe)) { WorkingDirectory=cache, UseShellExecute=false, CreateNoWindow=true,
            RedirectStandardInput=true, RedirectStandardOutput=true, RedirectStandardError=true };
        start.ArgumentList.Add(mod); start.ArgumentList.Add(Path.Combine(LocalData.Folder,"vocab.txt"));
        start.Environment["STS2_GAME_DIR"] = cache;
        // The parent points this to its initialized Godot extension. The standalone engine has no Godot scene tree.
        start.Environment.Remove("SENTRY_GODOT_LIB_PATH");
        start.Environment["DOTNET_EnableDiagnostics"] = "0";
        var process = Process.Start(start) ?? throw new IOException("탐색 프로세스를 시작할 수 없습니다.");
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (System.ComponentModel.Win32Exception) { }
        return process;
    }

    private static void Install(string source, string dest, bool link = false)
    {
        string temp = dest + "." + Guid.NewGuid().ToString("N");
        try {
            if (link && !OperatingSystem.IsWindows()) File.CreateSymbolicLink(temp, source);
            else if (!link || !CreateHardLink(temp,source,IntPtr.Zero)) File.Copy(source,temp);
            File.Move(temp,dest,true);
        } finally { File.Delete(temp); }
    }

    // Read only the localization tables the headless game requests. Never materialize the game pack.
    private static void ExtractEnglish(string path, string res, CancellationToken token)
    {
        using var stream = File.OpenRead(path);
        using var r = new BinaryReader(stream,Encoding.UTF8,true);
        if (r.ReadUInt32() != 0x43504447) throw new InvalidDataException("Godot pack");
        uint format = r.ReadUInt32();
        if (format is not (2 or 3)) throw new InvalidDataException("Godot pack version");
        stream.Position += 12;
        uint flags = r.ReadUInt32();
        if ((flags & 1) != 0) throw new InvalidDataException("Godot pack directory");
        long fileBase = r.ReadInt64();
        stream.Position = format == 3 ? r.ReadInt64() : stream.Position+64;
        int count = r.ReadInt32();
        if (count is < 0 or > 1000000) throw new InvalidDataException("Godot pack count");
        Directory.CreateDirectory(Path.Combine(res,"localization","eng"));
        for (int i=0; i<count; i++) {
            token.ThrowIfCancellationRequested();
            int length=r.ReadInt32();
            if(length is < 0 or > 16384) throw new InvalidDataException("Godot pack path");
            string name=Encoding.UTF8.GetString(r.ReadBytes(length)).TrimEnd('\0');
            if (name.StartsWith("res://",StringComparison.Ordinal)) name=name[6..];
            long offset=r.ReadInt64(), size=r.ReadInt64(); stream.Position+=16;
            uint entryFlags=r.ReadUInt32();
            if (!(name.StartsWith("localization/eng/",StringComparison.Ordinal) || name=="localization/completion.json")
                || !name.EndsWith(".json", StringComparison.Ordinal) || name.Contains("..") || name.Contains('\\')
                || name.Contains(':') || (entryFlags & 1)!=0) continue;
            if (size is < 0 or > 16000000) throw new InvalidDataException("Game localization length");
            long next=stream.Position;
            stream.Position=checked(offset+((flags & 2)!=0 ? fileBase : 0));
            var bytes=r.ReadBytes((int)size);
            if(bytes.Length!=size) throw new EndOfStreamException();
            File.WriteAllBytes(Path.Combine(res,name),bytes); stream.Position=next;
        }
    }
}
