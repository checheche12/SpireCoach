using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SpireCoach;

internal sealed record Pick(int Index, double TotalProbability, int CardCopies)
{
    internal double DisplayProbability => TotalProbability / CardCopies;
}

internal sealed class NativePolicy(string directory, int threads = 2) : IDisposable
{
    private readonly object sync = new();
    internal double VictoryValue { get; private set; }
    internal string? Checkpoint { get; private set; }
    private InferenceSession? session;
    private string? activeCharacter;
    private int format, tokenWidth, candidateWidth;
    private bool disposed;
    internal Pick[] Rank(string character, byte[] frame, int? perCopyActionKind = null, bool compareUpgradePreviews = false, string[]? labels = null)
        => Evaluate(character, frame, false, out _, perCopyActionKind, compareUpgradePreviews, labels: labels);

    internal Pick[] Evaluate(string character, byte[] frame, bool withValue, out float value,
        int? perCopyActionKind = null, bool compareUpgradePreviews = false, bool individual = false, int mode = 1, string[]? labels = null)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (session == null || character != activeCharacter)
            {
                session?.Dispose();
                session = null;
                Checkpoint = null;
                string modelDir = directory;
#if STS_114
                modelDir = Path.Combine(directory, "models", "v0.114.0");
#endif
                string path = Path.Combine(modelDir, character.ToLowerInvariant() + ".onnx");
                using var options = new SessionOptions { IntraOpNumThreads = threads, InterOpNumThreads = 1 };
                options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
                var loaded = new InferenceSession(path, options);
                try
                {
                    var metadata = loaded.ModelMetadata.CustomMetadataMap;
                    VictoryValue = metadata.TryGetValue("victory_value",out var victory)
                        ? double.Parse(victory,System.Globalization.CultureInfo.InvariantCulture) : 0;
                    if (metadata["character"] != character || !int.TryParse(metadata["obs_format"], out format))
                        throw new InvalidDataException("캐릭터와 모델 정보가 일치하지 않습니다.");
#if STS_114
                    if (metadata.GetValueOrDefault("catalog_version") != "v0.114.0")
                        throw new InvalidDataException("v0.114.0용 모델 카탈로그가 필요합니다.");
#endif
                    tokenWidth = loaded.InputMetadata["tf"].Dimensions[1];
                    candidateWidth = loaded.InputMetadata["cf"].Dimensions[1];
                    Checkpoint = metadata["checkpoint"];
                }
                catch { loaded.Dispose(); throw; }
                activeCharacter = character;
                session = loaded;
            }
            int offset = 0;
            T[] Read<T>(int count) where T : unmanaged
            {
                int bytes = checked(count * Marshal.SizeOf<T>());
                var result = MemoryMarshal.Cast<byte,T>(frame.AsSpan(offset, bytes)).ToArray();
                offset += bytes;
                return result;
            }
            var header = Read<int>(34);
            // The official protocol pads missing columns with zeros on older frames.
            bool stableFrame = header[0] == 22 && format == 28 && character is ("IRONCLAD" or "NECROBINDER");
            if (header[0] != format && !stableFrame) throw new InvalidDataException("모델과 게임 관측 형식이 일치하지 않습니다.");
            float[] ReadFeatures(int rows, int sourceWidth, int modelWidth)
            {
                var values = Read<float>(checked(rows * sourceWidth));
                if (sourceWidth == modelWidth) return values;
                if (sourceWidth > modelWidth) throw new InvalidDataException("모델 입력 너비 불일치");
                var padded = new float[checked(rows * modelWidth)];
                for (int row = 0; row < rows; row++)
                    Array.Copy(values, row * sourceWidth, padded, row * modelWidth, sourceWidth);
                return padded;
            }
            int n = header[2], m = header[3];
            var ti = Read<int>(n * 5);
            var tf = ReadFeatures(n, header[0] >= 27 ? 22 : 20, tokenWidth);
            offset += n * 2 * sizeof(float); // Training-only columns.
            var ci = Read<int>(m * 5);
            var cf = ReadFeatures(m, header[0] >= 26 ? 14 : 12, candidateWidth);
            static NamedOnnxValue Integers(string name, int[] values, int[] shape) =>
                NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(Array.ConvertAll(values, v => (long)v), shape));
            static NamedOnnxValue Floats(string name, float[] values, int[] shape) =>
                NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(values, shape));
            var inputs = new[] { Integers("header", header, [34]), Integers("ti", ti, [n,5]),
                Floats("tf", tf, [n,tokenWidth]), Integers("ci", ci, [m,5]), Floats("cf", cf, [m,candidateWidth]) };
            if (session.InputMetadata.ContainsKey("mode"))
                inputs = [.. inputs, Integers("mode", [mode], [])];
            else if (mode != 1) throw new InvalidDataException("Combat-practice input missing");
            using var results = session.Run(inputs, withValue ? new[] { "probabilities", "value" } : new[] { "probabilities" });
            value = withValue ? results.Last().AsEnumerable<float>().Single() : 0;
            if (!float.IsFinite(value)) throw new InvalidDataException("Invalid search value");
            if (withValue && VictoryValue <= 0) throw new InvalidDataException("Missing checkpoint victory value");
            var probabilities = results.First().AsEnumerable<float>().ToArray();
            if (probabilities.Length != m || probabilities.Any(p => !float.IsFinite(p) || p < 0)
                || Math.Abs(probabilities.Sum() - 1) > 0.001) throw new InvalidDataException("모델 확률 검증 실패");
            if (individual) return probabilities.Select((p, i) => new Pick(i, p, 1)).ToArray();
            // In combat B identifies the actual target. In deck upgrades B is a
            // separately allocated preview for each card, even for identical copies.
            string FeatureKey(float[] values, int row, int width) =>
                Convert.ToHexString(MemoryMarshal.AsBytes(values.AsSpan(row * width, width)));
            var children = Enumerable.Range(0, n).Where(i => ti[i * 5 + 3] >= 0).ToLookup(i => ti[i * 5 + 3]);
            string CardKey(int a)
            {
                if (a < 0) return "-";
                var extra = children[a].Select(k => $"{ti[k*5]}/{ti[k*5+1]}/{FeatureKey(tf,k,tokenWidth)}")
                    .Order(StringComparer.Ordinal);
                return $"{ti[a*5]}/{ti[a*5+1]}/{ti[a*5+2]}/{ti[a*5+4]}/{FeatureKey(tf,a,tokenWidth)}/{string.Join(',', extra)}";
            }
            return Enumerable.Range(0, m)
                .GroupBy(i =>
                {
                    int action = ci[i*5], target = ci[i*5+2];
                    bool preview = compareUpgradePreviews && action == perCopyActionKind && target >= 0;
                    return (Action: action, ci[i*5+3], Target: preview ? -1 : target,
                        Preview: preview ? CardKey(target) : "", Card: CardKey(ci[i*5+1]), FeatureKey(cf,i,candidateWidth), Label: labels?[i]);
                })
                .Select(group => new Pick(group.First(), group.Sum(i => (double)probabilities[i]),
                    group.Key.Action == perCopyActionKind ? group.Count() : 1))
                .OrderByDescending(pick => pick.DisplayProbability).ToArray();
        }
    }

    public void Dispose()
    {
        lock (sync) { disposed = true; session?.Dispose(); session = null; }
    }
}
