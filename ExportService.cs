using System.Globalization;
using System.Text;

namespace VNPC;

public static class ExportService
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static string F(double d) => d.ToString("0.######", Inv);

    public static async Task<(bool ok, string msg)> RunAsync(
        List<Clip> clips, string outPath, int w, int h, int fps, bool hw,
        IProgress<double> progress, CancellationToken ct, Action<string> status)
    {
        if (hw)
        {
            status("Експорт (Intel QSV)…");
            var r = await Once(clips, outPath, w, h, fps, true, progress, ct);
            if (r.ok) return r;
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
            status("QSV недоступний — експорт програмно…");
            progress.Report(0);
        }
        else
        {
            status("Експорт…");
        }
        return await Once(clips, outPath, w, h, fps, false, progress, ct);
    }

    static async Task<(bool ok, string msg)> Once(
        List<Clip> clips, string outPath, int w, int h, int fps, bool hw,
        IProgress<double> progress, CancellationToken ct)
    {
        var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-progress", "pipe:1", "-nostats" };

        // Швидке позиціонування на вході (-ss перед -i): не декодуємо файл з нуля для кожного кліпа.
        foreach (var c in clips)
        {
            args.Add("-ss"); args.Add(F(c.In));
            args.Add("-t"); args.Add(F(c.Out - c.In));
            args.Add("-i"); args.Add(c.Media.FilePath);
        }

        var sb = new StringBuilder();
        for (int i = 0; i < clips.Count; i++)
        {
            var c = clips[i];
            sb.Append($"[{i}:v]setpts=(PTS-STARTPTS)/{F(c.Speed)},fps={fps}," +
                      $"scale={w}:{h}:force_original_aspect_ratio=decrease," +
                      $"pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:black,setsar=1,format=yuv420p[v{i}];");

            if (c.Media.HasAudio)
            {
                sb.Append($"[{i}:a]asetpts=PTS-STARTPTS,{Atempo(c.Speed)}volume={F(c.Volume)}," +
                          $"aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo[a{i}];");
            }
            else
            {
                sb.Append($"anullsrc=r=48000:cl=stereo,atrim=duration={F(c.Duration)},asetpts=PTS-STARTPTS[a{i}];");
            }
        }
        for (int i = 0; i < clips.Count; i++) sb.Append($"[v{i}][a{i}]");
        sb.Append($"concat=n={clips.Count}:v=1:a=1[outv][outa]");

        args.Add("-filter_complex"); args.Add(sb.ToString());
        args.Add("-map"); args.Add("[outv]");
        args.Add("-map"); args.Add("[outa]");

        if (hw)
        {
            long px = (long)w * h;
            string br = px >= 1900000 ? "10M" : px >= 900000 ? "6M" : "3M";
            args.AddRange(new[] { "-c:v", "h264_qsv", "-b:v", br });
        }
        else
        {
            args.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p" });
        }
        args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", outPath });

        double total = clips.Sum(c => c.Duration);
        var (code, err) = await FfmpegService.RunAsync(args, line =>
        {
            if (line.StartsWith("out_time_us=") && long.TryParse(line.AsSpan(12), out var us) && total > 0)
                progress.Report(Math.Min(1.0, us / 1e6 / total));
        }, ct);

        if (code != 0) return (false, err);
        progress.Report(1.0);
        return (true, "");
    }

    /// <summary>atempo приймає 0.5–2, тому більші/менші швидкості складаємо з кількох фільтрів.</summary>
    static string Atempo(double speed)
    {
        if (Math.Abs(speed - 1.0) < 1e-6) return "";
        var sb = new StringBuilder();
        double rem = speed;
        while (rem > 2.0) { sb.Append("atempo=2,"); rem /= 2.0; }
        while (rem < 0.5) { sb.Append("atempo=0.5,"); rem /= 0.5; }
        if (Math.Abs(rem - 1.0) > 1e-6) sb.Append("atempo=" + F(rem) + ",");
        return sb.ToString();
    }
}
