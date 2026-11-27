using System.Net.Http;
using System.Security.Cryptography;

namespace SpireCoach;

internal static class LocalData
{
#if STS_114
    internal const int VocabSize = 2559;
    private const string Cache = "f77831b6-v0.114.0";
    private const string Source = "https://raw.githubusercontent.com/hmty-CHEN/AlphaDefect-Public/f77831b6ea77b8ece32daf1912154ae081da875c/game-v0.114.0/engine/catalog/";
    private const string VocabHash = "A6FA3596E6E4E570F3BD00AB596FCC6E5DD595D472A3EC08F9BC24ABA8079C8B";
    private const string AliasHash = "9D76E3B2EACBEB2DBA032553C3F53B4532EA8B2AA2F2CB6E273CDD874A3DCE78";
#else
    internal const int VocabSize = 2557;
    private const string Cache = "800db303";
    private const string Source = "https://raw.githubusercontent.com/hmty-CHEN/AlphaDefect-Public/800db3039162647bf0ad13ed379f155cc5806f5f/engine/catalog/";
    private const string VocabHash = "B9BA21326C3D90DC85284EB51BC6CD1B95739C56CABD458D502019DC7C6AB0A1";
    private const string AliasHash = "0A9FF41A14E58B998901CAE8D4157C4E8763892DBB1CEC1E9DAE2FAE050FF85A";
#endif
    internal static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpireCoach", "data", Cache);
    private static readonly Dictionary<string,string> Digests = new()
    {
        ["vocab.txt"] = VocabHash,
        ["aliases.txt"] = AliasHash
    };

    internal static bool Ready => Digests.All(entry =>
    {
        var path = Path.Combine(Folder, entry.Key);
        return File.Exists(path) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) == entry.Value;
    });

    internal static async Task Download()
    {
        Directory.CreateDirectory(Folder);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        foreach (var (file, digest) in Digests)
        {
            var bytes = await http.GetByteArrayAsync(Source + file).ConfigureAwait(false);
            if (Convert.ToHexString(SHA256.HashData(bytes)) != digest) throw new InvalidDataException("모델 어휘 검증 실패");
            var dest = Path.Combine(Folder, file);
            var temp = dest + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false); File.Move(temp, dest, true); }
            finally { File.Delete(temp); }
        }
    }
}
