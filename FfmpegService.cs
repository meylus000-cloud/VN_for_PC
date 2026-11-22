using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace VNPC;

public static class FfmpegService
{
    public static string Dir =>
        System.IO.Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    public static string Ffmpeg => System.IO.Path.Combine(Dir, "ffmpeg.exe");
    public static string Ffprobe => System.IO.Path.Combine(Dir, "ffprobe.exe");

    public static bool Available => File.Exists(Ffmpeg) && File.Exists(Ffprobe);

    /// <summary>Запускає ffmpeg/ffprobe, віддає рядки stdout, повертає код виходу і хвіст stderr.</summary>
    public static async Task<(int code, string errTail)> RunAsync(
        IEnumerable<string> args,
        Action<string> onStdoutLine,
        CancellationToken ct,
        bool lowPriority = false,
        string exe = null)
    {
        var psi = new ProcessStartInfo(exe ?? Ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var tail = new Queue<string>();

        p.OutputDataReceived += (s, e) =>
        {
            if (e.Data != null) onStdoutLine?.Invoke(e.Data);
        };
        p.ErrorDataReceived += (s, e) =>
        {
            if (e.Data == null) return;
            lock (tail)
            {
                tail.Enqueue(e.Data);
                while (tail.Count > 25) tail.Dequeue();
            }
        };

        p.Start();
        if (lowPriority)
        {
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var reg = ct.Register(() =>
        {
            try { if (!p.HasExited) p.Kill(true); } catch { }
        });

        await p.WaitForExitAsync();
        ct.ThrowIfCancellationRequested();

        string t;
        lock (tail) t = string.Join("\n", tail);
        return (p.ExitCode, t);
    }

    public static async Task<MediaItem> ProbeAsync(string path)
    {
        var sb = new StringBuilder();
        var (code, err) = await RunAsync(
            new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path },
            line => sb.AppendLine(line),
            CancellationToken.None, false, Ffprobe);

        if (code != 0)
            throw new Exception(string.IsNullOrWhiteSpace(err) ? "ffprobe: помилка читання файлу" : err);

        using var doc = JsonDocument.Parse(sb.ToString());
        var root = doc.RootElement;
        var m = new MediaItem { FilePath = path };

        if (root.TryGetProperty("format", out var fmt) &&
            fmt.TryGetProperty("duration", out var dv) &&
            double.TryParse(dv.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dur))
        {
            m.Duration = dur;
        }

        bool gotVideo = false;
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                string codecType = s.TryGetProperty("codec_type", out var ctv) ? ctv.GetString() : "";

                if (codecType == "video" && !gotVideo)
                {
                    if (s.TryGetProperty("disposition", out var disp) &&
                        disp.TryGetProperty("attached_pic", out var ap) &&
                        ap.ValueKind == JsonValueKind.Number && ap.GetInt32() == 1)
                        continue; // обкладинка, не відео

                    gotVideo = true;
                    int w = s.TryGetProperty("width", out var wv) ? wv.GetInt32() : 0;
                    int h = s.TryGetProperty("height", out var hv) ? hv.GetInt32() : 0;

                    if (s.TryGetProperty("side_data_list", out var sdl))
                    {
                        foreach (var sd in sdl.EnumerateArray())
                        {
                            if (sd.TryGetProperty("rotation", out var rot) && rot.ValueKind == JsonValueKind.Number)
                            {
                                int r = (int)Math.Abs(rot.GetDouble()) % 180;
                                if (r == 90) { var tmp = w; w = h; h = tmp; }
                            }
                        }
                    }
                    m.Width = w;
                    m.Height = h;

                    double fps = 0;
                    if (s.TryGetProperty("avg_frame_rate", out var af)) fps = ParseFps(af.GetString());
                    if (fps < 1 && s.TryGetProperty("r_frame_rate", out var rf)) fps = ParseFps(rf.GetString());
                    if (fps >= 1) m.Fps = fps;

                    if (m.Duration <= 0 &&
                        s.TryGetProperty("duration", out var sdur) &&
                        double.TryParse(sdur.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d2))
                        m.Duration = d2;
                }
                else if (codecType == "audio")
                {
                    m.HasAudio = true;
                }
            }
        }

        if (!gotVideo) throw new Exception("У файлі немає відеодоріжки");
        if (m.Duration <= 0) throw new Exception("Не вдалося визначити тривалість");
        return m;
    }

    static double ParseFps(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var parts = s.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && b > 0)
            return a / b;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
