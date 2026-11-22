using System.Security.Cryptography;
using System.Text;

namespace VNPC;

/// <summary>
/// Створює легкі копії відео (540p, часті ключові кадри, без B-кадрів) для плавного перегляду
/// на слабкому залізі. Експорт завжди йде з оригіналів.
/// </summary>
public static class ProxyService
{
    static readonly SemaphoreSlim Gate = new(1, 1); // по одному, щоб не душити слабкий процесор

    public static string ProxyDir =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VNPC", "proxy");

    public static async Task<string> BuildAsync(MediaItem m, Action<string> onStatus)
    {
        Directory.CreateDirectory(ProxyDir);

        var fi = new FileInfo(m.FilePath);
        string raw = m.FilePath + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
        string key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw))).Substring(0, 16);
        string outPath = System.IO.Path.Combine(ProxyDir, key + ".mp4");

        if (File.Exists(outPath) && new FileInfo(outPath).Length > 1024)
            return outPath;

        onStatus("Проксі: у черзі…");
        await Gate.WaitAsync();
        try
        {
            if (File.Exists(outPath) && new FileInfo(outPath).Length > 1024)
                return outPath;

            string tmp = outPath + ".part.mp4";
            if (File.Exists(tmp)) File.Delete(tmp);

            string vf = "scale=-2:'trunc(min(540,ih)/2)*2':flags=fast_bilinear";
            if (m.Fps > 30.5) vf = "fps=30," + vf;
            vf += ",format=yuv420p";

            var args = new List<string>
            {
                "-y", "-hide_banner", "-loglevel", "error", "-progress", "pipe:1", "-nostats",
                "-i", m.FilePath,
                "-map", "0:v:0",
            };
            if (m.HasAudio) { args.Add("-map"); args.Add("0:a:0"); }
            args.AddRange(new[]
            {
                "-vf", vf,
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "27",
                "-profile:v", "main", "-bf", "0", "-g", "12", "-keyint_min", "12", "-sc_threshold", "0",
                "-c:a", "aac", "-b:a", "96k", "-ac", "2",
                "-movflags", "+faststart",
                tmp
            });

            onStatus("Проксі 0%");
            int last = -1;
            var (code, err) = await FfmpegService.RunAsync(args, line =>
            {
                if (line.StartsWith("out_time_us=") &&
                    long.TryParse(line.AsSpan(12), out var us) && m.Duration > 0)
                {
                    int pct = (int)Math.Min(99, us / 1e6 / m.Duration * 100);
                    if (pct != last)
                    {
                        last = pct;
                        onStatus("Проксі " + pct + "%");
                    }
                }
            }, CancellationToken.None, lowPriority: true);

            if (code != 0 || !File.Exists(tmp))
                throw new Exception(err);

            File.Move(tmp, outPath, true);
            return outPath;
        }
        finally
        {
            Gate.Release();
        }
    }
}
