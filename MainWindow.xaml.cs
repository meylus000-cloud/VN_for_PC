using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace VNPC;

public partial class MainWindow : Window
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0x18, 0xC7, 0xA0));

    readonly ObservableCollection<MediaItem> _media = new();
    readonly List<Clip> _clips = new();
    Clip _selected;

    double _pps = 60;          // пікселів на секунду
    double _playhead;          // позиція курсора на доріжці, сек
    bool _playing;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };

    string _loadedSource;
    Clip _loadedClip;
    double _pendingSeek = -1;
    bool _pendingPlay;
    int _skipTicks;

    bool _ready;
    bool _syncing;

    double _ratioW = 16, _ratioH = 9;
    Rectangle _playheadLine;
    CancellationTokenSource _exportCts;

    enum Drag { None, Scrub, TrimL, TrimR }
    Drag _drag;
    Clip _dragClip;
    double _dragStartX, _origIn, _origOut;

    double Total => _clips.Sum(c => c.Duration);

    public MainWindow()
    {
        InitializeComponent();

        LstMedia.ItemsSource = _media;

        foreach (var s in new[] { "0.25x", "0.5x", "0.75x", "1x", "1.5x", "2x", "3x", "4x" }) CmbSpeed.Items.Add(s);
        CmbSpeed.SelectedIndex = 3;
        foreach (var r in new[] { "16:9", "9:16", "1:1", "4:5", "4:3" }) CmbRatio.Items.Add(r);
        CmbRatio.SelectedIndex = 0;
        foreach (var r in new[] { "1080p", "720p", "480p" }) CmbRes.Items.Add(r);
        CmbRes.SelectedIndex = 0;

        _timer.Tick += Timer_Tick;
        _ready = true;

        Loaded += (s, e) =>
        {
            UpdatePreviewFrame();
            Redraw();
            if (!FfmpegService.Available)
                MessageBox.Show(this,
                    "Поруч з програмою не знайдено ffmpeg.exe та ffprobe.exe.\nПоклади їх у ту саму папку, що й VNPC.exe.",
                    "VN PC", MessageBoxButton.OK, MessageBoxImage.Warning);
        };
    }

    // ───────────────────────── Імпорт ─────────────────────────

    async void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Відео|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.m4v;*.ts;*.mts;*.m2ts;*.wmv|Усі файли|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;
        await ImportFilesAsync(dlg.FileNames);
    }

    async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        await ImportFilesAsync(files);
    }

    async Task ImportFilesAsync(IEnumerable<string> files)
    {
        if (!FfmpegService.Available)
        {
            MessageBox.Show(this, "ffmpeg.exe / ffprobe.exe не знайдено поруч з програмою.", "VN PC");
            return;
        }
        foreach (var f in files)
        {
            try
            {
                SetStatus("Аналіз: " + System.IO.Path.GetFileName(f));
                var m = await FfmpegService.ProbeAsync(f);
                _media.Add(m);
                _ = StartProxyAsync(m);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не вдалося відкрити файл:\n" + f + "\n\n" + ex.Message, "VN PC");
            }
        }
        SetStatus("Готово");
    }

    async Task StartProxyAsync(MediaItem m)
    {
        try
        {
            string path = await ProxyService.BuildAsync(m,
                s => Dispatcher.BeginInvoke(new Action(() => m.Status = s)));
            m.ProxyPath = path;
            m.ProxyReady = true;
            m.Status = "Проксі готове ✓";

            // Якщо зараз показується цей файл і ми на паузі — перезавантажуємо з легкої копії.
            if (!_playing && _loadedClip != null && _loadedClip.Media == m)
            {
                _loadedSource = null;
                Seek(_playhead);
            }
        }
        catch (Exception)
        {
            m.Status = "Проксі: помилка (перегляд з оригіналу)";
        }
    }

    // ───────────────────────── Монтаж ─────────────────────────

    void BtnAddToTimeline_Click(object sender, RoutedEventArgs e)
    {
        if (LstMedia.SelectedItem is MediaItem m) AddMediaToTimeline(m);
        else SetStatus("Виберіть файл у медіатеці");
    }

    void LstMedia_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LstMedia.SelectedItem is MediaItem m) AddMediaToTimeline(m);
    }

    void AddMediaToTimeline(MediaItem m)
    {
        bool first = _clips.Count == 0;
        var c = new Clip { Media = m, In = 0, Out = m.Duration };
        _clips.Add(c);
        RecalcStarts();

        if (first && m.Width > 0 && m.Height > 0)
            CmbRatio.SelectedIndex = m.Height > m.Width ? 1 : 0; // 9:16 або 16:9

        SelectClip(c);
        Redraw();
        if (first) Seek(0);
        else UpdatePlayhead();
    }

    void RecalcStarts()
    {
        double t = 0;
        foreach (var c in _clips) { c.Start = t; t += c.Duration; }
    }

    Clip ClipAt(double t)
    {
        foreach (var c in _clips)
            if (t >= c.Start - 1e-6 && t < c.Start + c.Duration - 1e-6) return c;
        return null;
    }

    void SelectClip(Clip c)
    {
        _selected = c;
        _syncing = true;
        if (c != null)
        {
            SldVolume.Value = c.Volume;
            CmbSpeed.SelectedItem = c.Speed.ToString("0.##", Inv) + "x";
        }
        _syncing = false;
    }

    void BtnSplit_Click(object sender, RoutedEventArgs e) => SplitAtPlayhead();

    void SplitAtPlayhead()
    {
        var c = ClipAt(_playhead);
        if (c == null) return;
        double local = c.In + (_playhead - c.Start) * c.Speed;
        if (local <= c.In + 0.05 || local >= c.Out - 0.05)
        {
            SetStatus("Постав курсор усередині кліпа, щоб розрізати");
            return;
        }
        var n = c.Clone();
        n.In = local;
        c.Out = local;
        _clips.Insert(_clips.IndexOf(c) + 1, n);
        RecalcStarts();
        SelectClip(c);
        Redraw();
    }

    void BtnDelete_Click(object sender, RoutedEventArgs e) => DeleteSelected();

    void DeleteSelected()
    {
        if (_selected == null) return;
        _clips.Remove(_selected);
        _selected = null;
        RecalcStarts();
        Redraw();
        Seek(Math.Min(_playhead, Total));
    }

    void BtnMoveLeft_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    void BtnMoveRight_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

    void MoveSelected(int dir)
    {
        if (_selected == null) return;
        int i = _clips.IndexOf(_selected);
        int j = i + dir;
        if (j < 0 || j >= _clips.Count) return;
        (_clips[i], _clips[j]) = (_clips[j], _clips[i]);
        RecalcStarts();
        Redraw();
        Seek(_selected.Start);
    }

    void CmbSpeed_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _syncing || _selected == null || CmbSpeed.SelectedItem == null) return;
        string s = ((string)CmbSpeed.SelectedItem).TrimEnd('x');
        if (!double.TryParse(s, NumberStyles.Float, Inv, out var sp)) return;
        _selected.Speed = sp;
        RecalcStarts();
        Redraw();
        Seek(_selected.Start);
    }

    void SldVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _syncing || _selected == null) return;
        _selected.Volume = SldVolume.Value;
        if (_loadedClip == _selected) Player.Volume = Math.Min(1.0, _selected.Volume);
    }

    void CmbRatio_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || CmbRatio.SelectedItem == null) return;
        var p = ((string)CmbRatio.SelectedItem).Split(':');
        _ratioW = double.Parse(p[0], Inv);
        _ratioH = double.Parse(p[1], Inv);
        UpdatePreviewFrame();
    }

    void Combo_Closed(object sender, EventArgs e) => Focus();

    void SldZoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        _pps = SldZoom.Value;
        Redraw();
    }

    // ───────────────────────── Перегляд ─────────────────────────

    void PreviewHost_SizeChanged(object sender, SizeChangedEventArgs e) => UpdatePreviewFrame();

    void UpdatePreviewFrame()
    {
        double w = PreviewHost.ActualWidth, h = PreviewHost.ActualHeight;
        if (w <= 0 || h <= 0) return;
        double r = _ratioW / _ratioH;
        double fw = w, fh = w / r;
        if (fh > h) { fh = h; fw = h * r; }
        PreviewFrame.Width = Math.Max(2, Math.Floor(fw));
        PreviewFrame.Height = Math.Max(2, Math.Floor(fh));
    }

    void Seek(double t)
    {
        if (_clips.Count == 0)
        {
            _playhead = 0;
            _loadedClip = null;
            _loadedSource = null;
            _pendingSeek = -1;
            Player.Close();
            UpdatePlayhead();
            return;
        }
        t = Math.Max(0, Math.Min(t, Total - 0.001));
        _playhead = t;
        var c = ClipAt(t) ?? _clips[_clips.Count - 1];
        double src = c.In + (t - c.Start) * c.Speed;
        ShowClip(c, src, _playing);
        UpdatePlayhead();
    }

    void ShowClip(Clip c, double srcTime, bool play)
    {
        string path = c.Media.PlaybackPath;
        _loadedClip = c;
        _skipTicks = 3;

        if (_loadedSource != path)
        {
            _loadedSource = path;
            _pendingSeek = srcTime;
            _pendingPlay = play;
            Player.Source = new Uri(path);
            Player.Pause(); // змушує відкрити файл і показати кадр
        }
        else if (_pendingSeek >= 0)
        {
            // файл ще відкривається — просто оновлюємо ціль
            _pendingSeek = srcTime;
            _pendingPlay = play;
        }
        else
        {
            ApplyClipParams(c);
            Player.Position = TimeSpan.FromSeconds(srcTime);
            if (play) Player.Play(); else Player.Pause();
        }
    }

    void ApplyClipParams(Clip c)
    {
        Player.SpeedRatio = c.Speed;
        Player.Volume = Math.Min(1.0, c.Volume);
    }

    void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (_loadedClip == null) return;
        ApplyClipParams(_loadedClip);
        if (_pendingSeek >= 0)
        {
            Player.Position = TimeSpan.FromSeconds(_pendingSeek);
            _pendingSeek = -1;
        }
        if (_pendingPlay) Player.Play(); else Player.Pause();
        _skipTicks = 3;
    }

    void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        if (_playing && _loadedClip != null) AdvanceToNext(_loadedClip);
    }

    void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _pendingSeek = -1;
        _loadedSource = null;
        SetStatus("Не вдалося відтворити файл: " + e.ErrorException?.Message);
    }

    void Timer_Tick(object sender, EventArgs e)
    {
        if (!_playing || _loadedClip == null) return;
        if (_skipTicks > 0) { _skipTicks--; return; }
        if (_pendingSeek >= 0) return;

        var c = _loadedClip;
        double src = Player.Position.TotalSeconds;
        if (src >= c.Out - 0.03) { AdvanceToNext(c); return; }

        _playhead = Math.Max(c.Start, Math.Min(c.Start + (src - c.In) / c.Speed, c.Start + c.Duration));
        UpdatePlayhead();
    }

    void AdvanceToNext(Clip c)
    {
        int i = _clips.IndexOf(c) + 1;
        if (i <= 0) return;
        if (i >= _clips.Count)
        {
            PausePlayback();
            _playhead = Total;
            UpdatePlayhead();
            return;
        }

        var n = _clips[i];
        _playhead = n.Start;

        // Розрізані навпіл кліпи одного файлу йдуть безперервно, без перемотки — плавно.
        if (n.Media == c.Media && Math.Abs(n.In - c.Out) < 0.05 && Math.Abs(n.Speed - c.Speed) < 1e-6)
        {
            _loadedClip = n;
            ApplyClipParams(n);
            _skipTicks = 0;
        }
        else
        {
            ShowClip(n, n.In, true);
        }
        UpdatePlayhead();
    }

    void BtnPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_playing) PausePlayback(); else StartPlayback();
    }

    void BtnToStart_Click(object sender, RoutedEventArgs e)
    {
        if (_playing) PausePlayback();
        Seek(0);
    }

    void StartPlayback()
    {
        if (_clips.Count == 0) return;
        if (_playhead >= Total - 0.02) _playhead = 0;
        _playing = true;
        BtnPlay.Content = "⏸ Пауза";
        _timer.Start();
        Seek(_playhead);
    }

    void PausePlayback()
    {
        _playing = false;
        _timer.Stop();
        Player.Pause();
        BtnPlay.Content = "▶ Грати";
    }

    // ───────────────────────── Доріжка (малювання) ─────────────────────────

    void Redraw()
    {
        var cv = TimelineCanvas;
        cv.Children.Clear();

        double width = Math.Max(TimelineScroll.ViewportWidth, Total * _pps + 300);
        cv.Width = width;

        // лінійка
        double step = _pps >= 120 ? 0.5 : _pps >= 60 ? 1 : _pps >= 30 ? 2 : _pps >= 14 ? 5 : 10;
        for (double s = 0; s * _pps < width; s += step)
        {
            double x = s * _pps;
            var tick = new Rectangle { Width = 1, Height = 10, Fill = Brushes.Gray, IsHitTestVisible = false };
            Canvas.SetLeft(tick, x); Canvas.SetTop(tick, 22);
            cv.Children.Add(tick);

            var lbl = new TextBlock
            {
                Text = Util.Fmt(s, false), Foreground = Brushes.Gray, FontSize = 10, IsHitTestVisible = false
            };
            Canvas.SetLeft(lbl, x + 3); Canvas.SetTop(lbl, 6);
            cv.Children.Add(lbl);
        }

        // кліпи
        foreach (var c in _clips)
        {
            bool sel = c == _selected;
            double x = c.Start * _pps;
            double w = Math.Max(4, c.Duration * _pps - 2);

            string info = Util.Fmt(c.Duration);
            if (Math.Abs(c.Speed - 1) > 1e-6) info += "  ·  " + c.Speed.ToString("0.##", Inv) + "x";
            if (Math.Abs(c.Volume - 1) > 1e-6) info += "  ·  vol " + (int)Math.Round(c.Volume * 100) + "%";

            var panel = new StackPanel { Margin = new Thickness(8, 6, 8, 0) };
            panel.Children.Add(new TextBlock
            {
                Text = c.Media.Name, Foreground = Brushes.White, FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            panel.Children.Add(new TextBlock
            {
                Text = info, Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xD6, 0xDD)), FontSize = 11
            });

            var b = new Border
            {
                Width = w, Height = 100,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(sel ? Color.FromRgb(0x1D, 0x7A, 0x6A) : Color.FromRgb(0x2B, 0x4A, 0x68)),
                BorderBrush = sel ? Brushes.Gold : new SolidColorBrush(Color.FromRgb(0x44, 0x66, 0x88)),
                BorderThickness = new Thickness(sel ? 2 : 1),
                ClipToBounds = true,
                IsHitTestVisible = false,
                Child = panel
            };
            Canvas.SetLeft(b, x); Canvas.SetTop(b, 42);
            cv.Children.Add(b);

            if (sel)
            {
                foreach (double hx in new[] { x, x + w - 6 })
                {
                    var handle = new Rectangle
                    {
                        Width = 6, Height = 100, Fill = Brushes.Gold, Opacity = 0.9, IsHitTestVisible = false
                    };
                    Canvas.SetLeft(handle, hx); Canvas.SetTop(handle, 42);
                    cv.Children.Add(handle);
                }
            }
        }

        // курсор
        _playheadLine = new Rectangle { Width = 2, Height = 170, Fill = AccentBrush, IsHitTestVisible = false };
        Canvas.SetZIndex(_playheadLine, 100);
        cv.Children.Add(_playheadLine);
        UpdatePlayhead();
    }

    void UpdatePlayhead()
    {
        double x = _playhead * _pps;
        if (_playheadLine != null) Canvas.SetLeft(_playheadLine, x);
        TimeText.Text = Util.Fmt(_playhead) + " / " + Util.Fmt(Total);

        if (_playing)
        {
            double off = TimelineScroll.HorizontalOffset, vw = TimelineScroll.ViewportWidth;
            if (x > off + vw - 40) TimelineScroll.ScrollToHorizontalOffset(Math.Max(0, x - 80));
            else if (x < off) TimelineScroll.ScrollToHorizontalOffset(Math.Max(0, x - 80));
        }
    }

    // ───────────────────────── Доріжка (миша) ─────────────────────────

    void Timeline_Down(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(TimelineCanvas);
        if (_playing) PausePlayback();

        // лінійка — перемотування
        if (p.Y < 40)
        {
            _drag = Drag.Scrub;
            TimelineCanvas.CaptureMouse();
            Seek(Math.Max(0, p.X) / _pps);
            return;
        }

        // краї виділеного кліпа — обрізання
        if (_selected != null)
        {
            double l = _selected.Start * _pps;
            double r = (_selected.Start + _selected.Duration) * _pps;
            if (Math.Abs(p.X - l) <= 8) { StartTrim(Drag.TrimL, p.X); return; }
            if (Math.Abs(p.X - r) <= 8) { StartTrim(Drag.TrimR, p.X); return; }
        }

        var c = ClipAt(p.X / _pps);
        SelectClip(c);
        Redraw();
        Seek(Math.Max(0, p.X) / _pps);
    }

    void StartTrim(Drag kind, double x)
    {
        _drag = kind;
        _dragClip = _selected;
        _dragStartX = x;
        _origIn = _selected.In;
        _origOut = _selected.Out;
        TimelineCanvas.CaptureMouse();
    }

    void Timeline_Move(object sender, MouseEventArgs e)
    {
        if (_drag == Drag.None) return;
        var p = e.GetPosition(TimelineCanvas);

        if (_drag == Drag.Scrub)
        {
            Seek(Math.Max(0, p.X) / _pps);
            return;
        }

        double dt = (p.X - _dragStartX) / _pps;
        var c = _dragClip;
        if (_drag == Drag.TrimL)
            c.In = Math.Max(0, Math.Min(_origIn + dt * c.Speed, c.Out - 0.1));
        else
            c.Out = Math.Min(c.Media.Duration, Math.Max(_origOut + dt * c.Speed, c.In + 0.1));

        RecalcStarts();
        Redraw();
    }

    void Timeline_Up(object sender, MouseButtonEventArgs e)
    {
        var was = _drag;
        _drag = Drag.None;
        TimelineCanvas.ReleaseMouseCapture();
        if (was == Drag.TrimL || was == Drag.TrimR)
            Seek(Math.Min(_playhead, Total));
    }

    // ───────────────────────── Клавіатура ─────────────────────────

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is ComboBox) return;

        switch (e.Key)
        {
            case Key.Space:
                BtnPlay_Click(null, null);
                e.Handled = true;
                break;
            case Key.S:
                SplitAtPlayhead();
                e.Handled = true;
                break;
            case Key.Delete:
                DeleteSelected();
                e.Handled = true;
                break;
            case Key.Home:
                if (_playing) PausePlayback();
                Seek(0);
                e.Handled = true;
                break;
            case Key.Left:
                if (_playing) PausePlayback();
                Seek(_playhead - (Keyboard.Modifiers == ModifierKeys.Shift ? 1.0 : 0.04));
                e.Handled = true;
                break;
            case Key.Right:
                if (_playing) PausePlayback();
                Seek(_playhead + (Keyboard.Modifiers == ModifierKeys.Shift ? 1.0 : 0.04));
                e.Handled = true;
                break;
        }
    }

    // ───────────────────────── Експорт ─────────────────────────

    async void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        if (_clips.Count == 0)
        {
            MessageBox.Show(this, "Спочатку додай кліпи на доріжку.", "VN PC");
            return;
        }
        if (!FfmpegService.Available)
        {
            MessageBox.Show(this, "ffmpeg.exe / ffprobe.exe не знайдено поруч з програмою.", "VN PC");
            return;
        }

        var dlg = new SaveFileDialog
        {
            Filter = "MP4|*.mp4",
            FileName = "VN_PC_" + DateTime.Now.ToString("yyyyMMdd_HHmm", Inv) + ".mp4"
        };
        if (dlg.ShowDialog(this) != true) return;

        int shortSide = CmbRes.SelectedIndex == 0 ? 1080 : CmbRes.SelectedIndex == 1 ? 720 : 480;
        int w, h;
        if (_ratioW >= _ratioH)
        {
            h = shortSide;
            w = (int)Math.Round(h * _ratioW / _ratioH);
        }
        else
        {
            w = shortSide;
            h = (int)Math.Round(w * _ratioH / _ratioW);
        }
        w = w / 2 * 2;
        h = h / 2 * 2;

        int fps = (int)Math.Round(Math.Clamp(_clips.Max(c => c.Media.Fps), 24, 60));
        var snapshot = _clips.Select(c => c.Clone()).ToList();
        bool hw = ChkHw.IsChecked == true;

        if (_playing) PausePlayback();
        BtnExport.IsEnabled = false;
        ExportProgress.Value = 0;
        ExportProgress.Visibility = Visibility.Visible;
        BtnCancel.Visibility = Visibility.Visible;
        _exportCts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<double>(v => ExportProgress.Value = v);
            var res = await ExportService.RunAsync(snapshot, dlg.FileName, w, h, fps, hw, progress,
                _exportCts.Token, SetStatus);

            if (res.ok)
            {
                SetStatus("Експорт завершено: " + dlg.FileName);
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + dlg.FileName + "\"")
                    { UseShellExecute = true });
                }
                catch { }
            }
            else
            {
                SetStatus("Помилка експорту");
                MessageBox.Show(this, "Експорт не вдався:\n\n" + res.msg, "VN PC");
            }
        }
        catch (OperationCanceledException)
        {
            try { if (File.Exists(dlg.FileName)) File.Delete(dlg.FileName); } catch { }
            SetStatus("Експорт скасовано");
        }
        finally
        {
            BtnExport.IsEnabled = true;
            ExportProgress.Visibility = Visibility.Collapsed;
            BtnCancel.Visibility = Visibility.Collapsed;
            _exportCts?.Dispose();
            _exportCts = null;
        }
    }

    void BtnCancel_Click(object sender, RoutedEventArgs e) => _exportCts?.Cancel();

    void SetStatus(string s)
    {
        if (Dispatcher.CheckAccess()) StatusText.Text = s;
        else Dispatcher.BeginInvoke(new Action(() => StatusText.Text = s));
    }
}
