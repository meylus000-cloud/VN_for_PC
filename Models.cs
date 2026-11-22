using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace VNPC;

public static class Util
{
    public static string Fmt(double s, bool tenths = true)
    {
        if (s < 0) s = 0;
        int m = (int)(s / 60);
        double r = s - m * 60;
        if (tenths)
            return m + ":" + r.ToString("00.0", CultureInfo.InvariantCulture);
        return m + ":" + ((int)r).ToString("00", CultureInfo.InvariantCulture);
    }
}

/// <summary>Файл у медіатеці.</summary>
public class MediaItem : INotifyPropertyChanged
{
    public string FilePath { get; set; }
    public string Name => System.IO.Path.GetFileName(FilePath);
    public double Duration { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; } = 30;
    public bool HasAudio { get; set; }

    string _proxyPath;
    bool _proxyReady;
    string _status = "";

    public string ProxyPath
    {
        get => _proxyPath;
        set { _proxyPath = value; OnChanged(); }
    }

    public bool ProxyReady
    {
        get => _proxyReady;
        set { _proxyReady = value; OnChanged(); }
    }

    public string Status
    {
        get => _status;
        set { _status = value; OnChanged(); }
    }

    /// <summary>Що відтворює попередній перегляд: проксі (легкий) або оригінал.</summary>
    public string PlaybackPath =>
        _proxyReady && !string.IsNullOrEmpty(_proxyPath) && File.Exists(_proxyPath) ? _proxyPath : FilePath;

    public string Info =>
        Util.Fmt(Duration) + "  ·  " + Width + "×" + Height + "  ·  " +
        Fps.ToString("0.#", CultureInfo.InvariantCulture) + " fps";

    public event PropertyChangedEventHandler PropertyChanged;
    void OnChanged([CallerMemberName] string name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Кліп на доріжці (магнітна доріжка: кліпи йдуть один за одним без пропусків).</summary>
public class Clip
{
    public MediaItem Media { get; set; }
    public double In { get; set; }      // початок у джерелі, сек
    public double Out { get; set; }     // кінець у джерелі, сек
    public double Speed { get; set; } = 1.0;
    public double Volume { get; set; } = 1.0;
    public double Start { get; set; }   // позиція на доріжці, сек (рахується автоматично)

    public double Duration => (Out - In) / Speed;

    public Clip Clone() => (Clip)MemberwiseClone();
}
