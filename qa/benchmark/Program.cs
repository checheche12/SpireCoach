using System.Diagnostics;
using SpireCoach;

var frames = new[] { "combat", "after-card", "reward" }
    .Select(name => File.ReadAllBytes(Path.Combine(args[1], name + ".bin"))).ToArray();
using var process = Process.GetCurrentProcess();
string character = Path.GetFileNameWithoutExtension(args[0]).ToUpperInvariant();
using var policy = new NativePolicy(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
process.Refresh();
long startMemory = process.WorkingSet64;
var timer = Stopwatch.StartNew();
policy.Rank(character, frames[0]);
Console.WriteLine($"First recommendation {timer.Elapsed.TotalMilliseconds:F1} ms");
var samples = new List<double>();
for (int i = 0; i < 120; i++)
{
    timer.Restart();
    var result = policy.Rank(character, frames[i % frames.Length]);
    samples.Add(timer.Elapsed.TotalMilliseconds);
    if (result.Length == 0) throw new InvalidDataException();
}
process.Refresh();
samples.Sort();
Console.WriteLine($"Median {samples[60]:F2} ms, p95 {samples[113]:F2} ms");
Console.WriteLine($"Working set increase {(process.WorkingSet64 - startMemory) / 1e6:F1} MB, peak increase {(process.PeakWorkingSet64 - startMemory) / 1e6:F1} MB");
