using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FNBoost.Core;
using FNBoost.Crosshair;
using FNBoost.Perf;

namespace FNBoost.Views.Pages
{
    /// <summary>Voce di una ComboBox: testo mostrato + valore.</summary>
    public sealed class PerfOption
    {
        public string Label { get; init; } = "";
        public object Value { get; init; } = "";
    }

    /// <summary>Riga dello storico sessioni (testi già formattati).</summary>
    public sealed class SessionRow
    {
        public PerfSession Session { get; init; } = null!;
        public string Title { get; init; } = "";
        public string Detail { get; init; } = "";
        public string AvgText { get; init; } = "";
        public string LowText { get; init; } = "";
    }

    /// <summary>Osservazione dell'analisi con l'icona, per lo stesso modello grafico dei controlli della Panoramica.</summary>
    public sealed class InsightRow
    {
        public CheckStatus Severity { get; init; }
        public string Title { get; init; } = "";
        public string Message { get; init; } = "";
        public string Hint { get; init; } = "";

        public string Icon => Severity switch
        {
            CheckStatus.Ok => "✔",
            CheckStatus.Info => "ℹ",
            CheckStatus.Warn => "⚠",
            _ => "✖"
        };

        public static InsightRow From(PerfInsight i) =>
            new() { Severity = i.Severity, Title = i.Title, Message = i.Message, Hint = i.Hint };
    }

    /// <summary>Un numero della griglia statistiche di una sessione, con spiegazione nel tooltip.</summary>
    public sealed class StatItem
    {
        public string Label { get; init; } = "";
        public string Value { get; init; } = "";
        public string Tip { get; init; } = "";
        public Brush Brush { get; init; } = Brushes.White;
    }

    public partial class PerformancePage : UserControl
    {
        private static readonly string[] Palette =
        {
            "#FFFFFF", "#00E5FF", "#00FF66", "#FFFF00", "#FF8A00", "#FF2D55", "#FF4DFF", "#7C5CFF"
        };

        private readonly DispatcherTimer _settingsTimer;
        private bool _subscribed;
        private bool _loadingSettings;
        private bool _refreshQueued;
        private int _analysisSeq;
        private int? _refreshHz;
        private PerfSession? _selected;
        private List<PerfSession> _sessions = new();

        private static PerfService? P => App.Perf;
        private static CultureInfo C => CultureInfo.CurrentCulture;

        public PerformancePage()
        {
            InitializeComponent();

            // Salvataggio delle impostazioni di misura con un piccolo ritardo (lo slider genera molti eventi).
            _settingsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _settingsTimer.Tick += (_, _) =>
            {
                _settingsTimer.Stop();
                ApplyPerfSettings();
            };

            if (P == null)
            {
                // Modulo non disponibile (errore all'avvio, vedi registro): la pagina resta consultabile ma inerte.
                StatusLine.Text = "Modulo prestazioni non disponibile: controlla il registro attività.";
                StatusDot.Fill = Res("BadBrush");
                IsEnabled = false;
                return;
            }

            TargetCombo.ItemsSource = new[]
            {
                new PerfOption { Label = "Fortnite", Value = PerfTarget.Fortnite },
                new PerfOption { Label = "Qualsiasi gioco in primo piano", Value = PerfTarget.Foreground }
            };
            WindowCombo.ItemsSource = new[] { 10, 30, 60, 120 }
                .Select(s => new PerfOption { Label = $"Ultimi {s} secondi", Value = s }).ToArray();
            MinSessionCombo.ItemsSource = new[]
            {
                new PerfOption { Label = "15 secondi", Value = 15 },
                new PerfOption { Label = "30 secondi", Value = 30 },
                new PerfOption { Label = "1 minuto", Value = 60 },
                new PerfOption { Label = "2 minuti", Value = 120 },
                new PerfOption { Label = "5 minuti", Value = 300 }
            };
            CornerCombo.ItemsSource = new[]
            {
                new PerfOption { Label = "In alto a sinistra", Value = OverlayCorner.TopLeft },
                new PerfOption { Label = "In alto a destra", Value = OverlayCorner.TopRight },
                new PerfOption { Label = "In basso a sinistra", Value = OverlayCorner.BottomLeft },
                new PerfOption { Label = "In basso a destra", Value = OverlayCorner.BottomRight }
            };

            OverlayCard.DataContext = App.Settings.Perf.Overlay;
            AddSwatches(TextSwatches, hex => App.Settings.Perf.Overlay.TextColor = hex);
            AddSwatches(AccentSwatches, hex => App.Settings.Perf.Overlay.AccentColor = hex);
            OverlayHotkeyHint.Text = $"Scorciatoia globale: {App.Settings.HotkeyOverlay}";
            RecordHotkeyHint.Text = $"Scorciatoia per avviare/fermare una registrazione: {App.Settings.HotkeyRecord}";

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            IsVisibleChanged += OnVisibleChanged;
        }

        private static Brush Res(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

        private void AddSwatches(Panel host, Action<string> apply)
        {
            foreach (var hex in Palette)
            {
                var b = new Button
                {
                    Style = (Style)FindResource("Swatch"),
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                    Width = 20,
                    Height = 20,
                    Margin = new Thickness(0, 0, 5, 4),
                    ToolTip = hex
                };
                b.Click += (_, _) => apply(hex);
                host.Children.Add(b);
            }
        }

        // ================= Ciclo di vita =================

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            var p = P;
            if (p == null) return;
            if (!_subscribed)
            {
                // La pagina resta in cache: ci si aggancia agli eventi solo mentre è visibile.
                p.LiveUpdated += OnLive;
                p.StatusChanged += OnStatusChanged;
                p.SessionSaved += OnSessionSaved;
                p.Store.Changed += OnStoreChanged;
                _subscribed = true;
            }
            LoadSettingsUi();
            RefreshMonitors();
            OnStatusChanged();
            OnLive(p.Live);
            QueueRefresh();

            if (_refreshHz == null)
            {
                try
                {
                    _refreshHz = await Task.Run(() =>
                    {
                        var list = SystemDiagnostics.ReadDisplayList();
                        var d = list.FirstOrDefault(x => x.Primary) ?? list.FirstOrDefault();
                        return d?.CurrentHz ?? 0;
                    });
                }
                catch (Exception ex)
                {
                    _refreshHz = 0;
                    Log.Warn("Lettura frequenza monitor: " + ex.Message);
                }
                ApplyRefreshRate();
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_settingsTimer.IsEnabled)
            {
                _settingsTimer.Stop();
                ApplyPerfSettings();
            }
            var p = P;
            if (!_subscribed || p == null) return;
            p.LiveUpdated -= OnLive;
            p.StatusChanged -= OnStatusChanged;
            p.SessionSaved -= OnSessionSaved;
            p.Store.Changed -= OnStoreChanged;
            _subscribed = false;
        }

        private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // Finestra principale nascosta nell'area di notifica: niente aggiornamenti; al ritorno si riallinea subito.
            if (IsVisible && P is { } p)
            {
                OnStatusChanged();
                OnLive(p.Live);
            }
        }

        private void ApplyRefreshRate()
        {
            int hz = _refreshHz ?? 0;
            if (hz <= 1) return;
            FpsChart.ReferenceValue = hz;
            FpsChart.ReferenceLabel = $"monitor {hz} Hz";
            FtGraph.TargetMs = 1000.0 / hz;
            FtGraph.TargetLabel = $"{hz} Hz = {(1000.0 / hz).ToString("0.0", C)} ms";
        }

        // ================= Dal vivo =================

        private static string N0(double v) => v > 0 && !double.IsInfinity(v) ? v.ToString("0", C) : "–";

        private static string Dur(double sec) =>
            TimeSpan.FromSeconds(Math.Max(0, sec)).ToString(sec >= 3600 ? @"h\:mm\:ss" : @"m\:ss", C);

        private static string DisplayName(string? process) =>
            string.IsNullOrEmpty(process) ? "–" :
            string.Equals(process, FortniteLocator.ClientProcessName, StringComparison.OrdinalIgnoreCase) ? "Fortnite" : process;

        private void OnStatusChanged()
        {
            var p = P;
            if (p == null) return;
            var live = p.Live;
            StatusLine.Text = string.IsNullOrEmpty(live.StatusText) ? "…" : live.StatusText;
            StatusDot.Fill = Res(live.Status switch
            {
                CaptureStatus.Capturing => "OkBrush",
                CaptureStatus.WaitingForGame => "WarnBrush",
                CaptureStatus.Error => "BadBrush",
                _ => "MutedBrush"
            });
        }

        private void OnLive(LiveSnapshot snap)
        {
            if (!IsVisible) return;
            if (StatusLine.Text != snap.StatusText && !string.IsNullOrEmpty(snap.StatusText)) OnStatusChanged();

            var w = snap.Window;
            bool data = snap.HasData;
            bool stats = data && w.HasData;
            FpsNowText.Text = data ? N0(snap.CurrentFps) : "–";
            AvgText.Text = stats ? N0(w.AvgFps) : "–";
            Low1Text.Text = stats ? N0(w.Low1Fps) : "–";
            Low1Sub.Text = stats && w.AvgFps > 0 ? $"{(w.Low1Fps / w.AvgFps * 100).ToString("0", C)}% della media" : "frame più lenti";
            Low01Text.Text = stats ? N0(w.Low01Fps) : "–";
            MinText.Text = stats ? N0(w.MinFps) : "–";
            MaxText.Text = stats ? N0(w.MaxFps) : "–";
            FtText.Text = data && snap.LastFrametimeMs > 0 ? snap.LastFrametimeMs.ToString("0.0", C) + " ms" : "–";
            FtSub.Text = stats ? $"media {w.AvgFrametimeMs.ToString("0.0", C)} ms" : "ultimo frame";
            StutterText.Text = stats ? w.StuttersPerMin.ToString("0.0", C) : "–";
            StutterSub.Text = stats ? $"{w.Stutters} nella finestra" : "scatti";
            ConsText.Text = stats ? w.ConsistencyScore.ToString("0", C) : "–";
            ConsText.Foreground = Res(!stats ? "TextBrush" : w.ConsistencyScore >= 80 ? "OkBrush" : w.ConsistencyScore >= 60 ? "WarnBrush" : "BadBrush");
            WindowSub.Text = P != null ? $"ultimi {P.Settings.WindowSeconds} s" : "finestra mobile";
            GameText.Text = data ? DisplayName(snap.ProcessName) : "–";
            GameSub.Text = data && snap.ProcessId is int pid ? $"PID {pid}" : "in attesa";
            ProcessLine.Text = data && w.HasData
                ? $"{DisplayName(snap.ProcessName)} · {w.Frames.ToString("N0", C)} frame negli ultimi {w.DurationSec.ToString("0", C)} s"
                : "";

            CpuText.Text = snap.CpuPercent.ToString("0", C) + "%";
            CpuScale.ScaleX = Math.Clamp(snap.CpuPercent / 100, 0, 1);
            GpuText.Text = snap.GpuPercent is { } g ? g.ToString("0", C) + "%" : "–";
            GpuScale.ScaleX = Math.Clamp((snap.GpuPercent ?? 0) / 100, 0, 1);
            RamText.Text = snap.RamPercent.ToString("0", C) + "%";
            RamScale.ScaleX = Math.Clamp(snap.RamPercent / 100, 0, 1);
            if (snap.VramUsedGb is { } used)
            {
                VramText.Text = snap.VramTotalGb is { } tot && tot > 0
                    ? $"{used.ToString("0.0", C)} / {tot.ToString("0", C)} GB"
                    : $"{used.ToString("0.0", C)} GB";
                VramScale.ScaleX = snap.VramTotalGb is { } t2 && t2 > 0 ? Math.Clamp(used / t2, 0, 1) : 0;
            }
            else
            {
                VramText.Text = "–";
                VramScale.ScaleX = 0;
            }

            FtGraph.Frametimes = snap.RecentFrametimes;
            FpsChart.Values = snap.FpsHistory;
            UpdateRecordUi(snap.IsRecording, snap.RecordingSeconds);
        }

        private void UpdateRecordUi(bool recording, double seconds)
        {
            RecordIcon.Text = recording ? "" : "";
            RecordText.Text = recording ? $"Ferma registrazione ({Dur(seconds)})" : "Avvia registrazione";
            RecBadge.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
            RecBadgeText.Text = $"● REC {Dur(seconds)}";
        }

        private void Record_Click(object sender, RoutedEventArgs e)
        {
            var p = P;
            if (p == null) return;
            try
            {
                if (p.IsRecording)
                {
                    var s = p.StopRecording();
                    RecordHint.Text = s != null
                        ? $"Sessione salvata: {s.Title} ({s.DurationText}). La trovi nello storico qui sotto."
                        : $"Registrazione fermata ma non salvata: troppo breve (minimo {p.Settings.MinSessionSeconds} s) o senza frame del gioco.";
                    UpdateRecordUi(false, 0);
                }
                else
                {
                    p.StartRecording(LabelBox.Text);
                    RecordHint.Text = string.IsNullOrWhiteSpace(LabelBox.Text)
                        ? "Registrazione avviata. Gioca normalmente, poi premi «Ferma registrazione»."
                        : $"Registrazione «{LabelBox.Text.Trim()}» avviata.";
                    UpdateRecordUi(p.IsRecording, 0);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Registrazione sessione", ex);
                RecordHint.Text = "Operazione non riuscita: " + ex.Message;
            }
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            P?.ResetLive();
            RecordHint.Text = "Statistiche dal vivo azzerate.";
        }

        // ================= Impostazioni di misura =================

        private void LoadSettingsUi()
        {
            var s = App.Settings.Perf;
            _loadingSettings = true;
            try
            {
                CaptureToggle.IsChecked = s.CaptureEnabled;
                AutoRecordToggle.IsChecked = s.AutoRecord;
                TargetCombo.SelectedValue = s.Target;
                WindowCombo.SelectedValue = s.WindowSeconds;
                MinSessionCombo.SelectedValue = s.MinSessionSeconds;
                StutterSlider.Value = Math.Clamp(s.StutterFactor, StutterSlider.Minimum, StutterSlider.Maximum);
                StutterValue.Text = StutterSlider.Value.ToString("0.##", C) + "×";
            }
            finally
            {
                _loadingSettings = false;
            }
        }

        private void PerfSetting_Click(object sender, RoutedEventArgs e) => ScheduleApply();

        private void PerfSetting_Changed(object sender, SelectionChangedEventArgs e) => ScheduleApply();

        private void Stutter_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (StutterValue != null) StutterValue.Text = e.NewValue.ToString("0.##", C) + "×";
            ScheduleApply();
        }

        private void ScheduleApply()
        {
            if (_loadingSettings || !IsLoaded) return;
            _settingsTimer.Stop();
            _settingsTimer.Start();
        }

        /// <summary>Scrive le impostazioni, salva e chiede al PerfService di rileggerle (riavvia ETW solo se serve).</summary>
        private void ApplyPerfSettings()
        {
            var p = P;
            if (p == null) return;
            var s = App.Settings.Perf;
            s.CaptureEnabled = CaptureToggle.IsChecked == true;
            s.AutoRecord = AutoRecordToggle.IsChecked == true;
            if (TargetCombo.SelectedValue is PerfTarget t) s.Target = t;
            if (WindowCombo.SelectedValue is int ws) s.WindowSeconds = ws;
            if (MinSessionCombo.SelectedValue is int ms) s.MinSessionSeconds = ms;
            s.StutterFactor = Math.Round(StutterSlider.Value, 2);
            App.Settings.Save();
            try
            {
                p.Restart();
            }
            catch (Exception ex)
            {
                Log.Error("Riavvio contatore FPS", ex);
            }
            OnStatusChanged();
        }

        // ================= Overlay =================

        private void RefreshMonitors()
        {
            try
            {
                var o = App.Settings.Perf.Overlay;
                var monitors = Monitors.GetAll();
                MonitorCombo.ItemsSource = monitors;
                if (string.IsNullOrEmpty(o.Monitor) || monitors.All(m => m.Device != o.Monitor))
                    o.Monitor = monitors.FirstOrDefault(m => m.Primary)?.Device ?? "";
            }
            catch (Exception ex)
            {
                Log.Warn("Elenco monitor: " + ex.Message);
            }
        }

        // ================= Storico sessioni =================

        private void OnStoreChanged()
        {
            // Può arrivare da qualsiasi thread (salvataggio in background).
            try
            {
                Dispatcher.BeginInvoke(new Action(QueueRefresh));
            }
            catch (Exception ex)
            {
                Log.Warn("Aggiornamento storico: " + ex.Message);
            }
        }

        private void OnSessionSaved(PerfSession session) => QueueRefresh();

        /// <summary>Un solo aggiornamento anche se arrivano più notifiche di fila.</summary>
        private void QueueRefresh()
        {
            if (_refreshQueued) return;
            _refreshQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () => await RefreshSessionsAsync()));
        }

        private async Task RefreshSessionsAsync()
        {
            _refreshQueued = false;
            var p = P;
            if (p == null) return;
            try
            {
                var store = p.Store;
                // La prima lettura apre tutti i file JSON: meglio fuori dal thread della UI.
                var list = await Task.Run(() => store.List().ToList());

                _sessions = list;
                var keepId = _selected?.Id;
                var rows = list.Select(ToRow).ToList();
                SessionList.ItemsSource = rows;
                EmptySessionsText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (keepId != null)
                {
                    var row = rows.FirstOrDefault(r => r.Session.Id == keepId);
                    if (row != null) SessionList.SelectedItem = row;
                    else HideAnalysis();
                }
                UpdateTrend();
                UpdateSessionButtons();
            }
            catch (Exception ex)
            {
                Log.Error("Lettura storico sessioni", ex);
            }
        }

        private static SessionRow ToRow(PerfSession s)
        {
            var st = s.Stats ?? new FrameStatsResult();
            return new SessionRow
            {
                Session = s,
                Title = s.Title,
                Detail = $"{s.DurationText} · {DisplayName(s.ProcessName)}" +
                         (string.IsNullOrEmpty(s.RenderMode) ? "" : $" · {s.RenderMode}") +
                         (st.HasData ? $" · consistenza {N0(st.ConsistencyScore)}" : ""),
                AvgText = st.HasData ? $"{N0(st.AvgFps)} FPS" : "–",
                LowText = st.HasData ? $"1% low {N0(st.Low1Fps)}" : ""
            };
        }

        private void UpdateTrend()
        {
            var valid = _sessions.Where(s => s.Stats != null && s.Stats.HasData).ToList();
            var newest = valid.OrderByDescending(s => s.StartedAt).FirstOrDefault();
            if (newest == null)
            {
                TrendChart.Values = null;
                TrendChart.Values2 = null;
                TrendSub.Text = "Servono sessioni registrate.";
            }
            else
            {
                // Solo il gioco dell'ultima sessione: mescolare giochi diversi non avrebbe senso.
                var group = valid
                    .Where(s => string.Equals(s.ProcessName, newest.ProcessName, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(s => s.StartedAt)
                    .ToList();
                if (group.Count > 40) group = group.Skip(group.Count - 40).ToList();
                TrendChart.Values = group.Select(s => s.Stats.AvgFps).ToArray();
                TrendChart.Values2 = group.Select(s => s.Stats.Low1Fps).ToArray();
                TrendSub.Text = $"{DisplayName(newest.ProcessName)} · {group.Count} sessioni" +
                                (group.Count > 1 ? $", dal {group[0].StartedAt.ToString("d MMM", C)} al {group[^1].StartedAt.ToString("d MMM", C)}" : "");
            }
            try
            {
                TrendList.ItemsSource = PerfAnalyzer.Trend(_sessions).Select(InsightRow.From).ToList();
            }
            catch (Exception ex)
            {
                Log.Error("Andamento sessioni", ex);
                TrendList.ItemsSource = null;
            }
        }

        private void UpdateSessionButtons()
        {
            bool sel = SessionList.SelectedItem is SessionRow;
            AnalyzeBtn.IsEnabled = sel;
            RenameBtn.IsEnabled = sel;
            ExportBtn.IsEnabled = sel;
            DeleteBtn.IsEnabled = sel;
        }

        private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSessionButtons();
            if (SessionList.SelectedItem is not SessionRow row) return;
            // Stessa sessione ricaricata dall'archivio (es. dopo Rinomina): si aggiornano solo i testi.
            bool same = _selected != null && _selected.Id == row.Session.Id;
            ShowAnalysis(row.Session, reload: !same);
        }

        private void SessionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (SessionList.SelectedItem is SessionRow) AnalysisCard.BringIntoView();
        }

        private void Analyze_Click(object sender, RoutedEventArgs e)
        {
            if (SessionList.SelectedItem is not SessionRow row) return;
            if (_selected?.Id != row.Session.Id) ShowAnalysis(row.Session, reload: true);
            AnalysisCard.BringIntoView();
        }

        private async void Rename_Click(object sender, RoutedEventArgs e)
        {
            var p = P;
            if (p == null || SessionList.SelectedItem is not SessionRow row) return;
            var label = LabelBox.Text.Trim();
            if (label.Length == 0)
            {
                SessionHint.Text = "Scrivi il nuovo nome nel campo «Etichetta» in alto, poi premi di nuovo Rinomina.";
                LabelBox.Focus();
                return;
            }
            var session = row.Session;
            session.Label = label;
            try
            {
                var store = p.Store;
                await Task.Run(() => store.UpdateMeta(session));
                SessionHint.Text = $"Sessione rinominata in «{label}».";
            }
            catch (Exception ex)
            {
                Log.Error("Rinomina sessione", ex);
                SessionHint.Text = "Rinomina non riuscita: " + ex.Message;
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            var p = P;
            if (p == null || SessionList.SelectedItem is not SessionRow row) return;
            var owner = Window.GetWindow(this);
            var msg = $"Eliminare la sessione «{row.Session.Title}»?\n\nVengono cancellati statistiche e frametime: l'operazione non si può annullare.";
            var res = owner != null
                ? MessageBox.Show(owner, msg, "FN Boost", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
                : MessageBox.Show(msg, "FN Boost", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (res != MessageBoxResult.Yes) return;
            try
            {
                p.Store.Delete(row.Session.Id);
                Log.Info($"Sessione eliminata: {row.Session.Title}");
                HideAnalysis();
                SessionHint.Text = "Sessione eliminata.";
            }
            catch (Exception ex)
            {
                Log.Error("Eliminazione sessione", ex);
            }
        }

        private async void Export_Click(object sender, RoutedEventArgs e)
        {
            var p = P;
            if (p == null || SessionList.SelectedItem is not SessionRow row) return;
            var session = row.Session;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Esporta frametime della sessione",
                Filter = "File CSV (*.csv)|*.csv",
                DefaultExt = ".csv",
                FileName = $"FNBoost-{session.Id}.csv"
            };
            var owner = Window.GetWindow(this);
            if ((owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog()) != true) return;

            var path = dlg.FileName;
            var store = p.Store;
            ExportBtn.IsEnabled = false;
            SessionHint.Text = "Esportazione in corso…";
            try
            {
                int frames = await Task.Run(() => WriteCsv(store, session.Id, path));
                SessionHint.Text = $"Esportati {frames.ToString("N0", C)} frame in {Path.GetFileName(path)}.";
                Log.Info($"Sessione esportata in CSV: {path}");
            }
            catch (Exception ex)
            {
                Log.Error("Esportazione CSV", ex);
                SessionHint.Text = "Esportazione non riuscita: " + ex.Message;
            }
            finally
            {
                UpdateSessionButtons();
            }
        }

        /// <summary>Un frame per riga: indice, istante di fine frame (ms dall'inizio), frametime e FPS istantanei. Formato invariante.</summary>
        private static int WriteCsv(PerfSessionStore store, string id, string path)
        {
            var ft = store.LoadFrametimes(id) ?? throw new InvalidOperationException("frametime della sessione non trovati");
            var inv = CultureInfo.InvariantCulture;
            using var w = new StreamWriter(path, false, new UTF8Encoding(false));
            w.WriteLine("index,time_ms,frametime_ms,fps");
            double t = 0;
            var sb = new StringBuilder(64);
            for (int i = 0; i < ft.Length; i++)
            {
                double f = ft[i];
                t += f;
                sb.Clear();
                sb.Append(i.ToString(inv)).Append(',')
                  .Append(t.ToString("0.###", inv)).Append(',')
                  .Append(f.ToString("0.####", inv)).Append(',')
                  .Append(f > 0 ? (1000.0 / f).ToString("0.##", inv) : "0");
                w.WriteLine(sb);
            }
            return ft.Length;
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            var p = P;
            if (p == null) return;
            try
            {
                Directory.CreateDirectory(p.Store.DirectoryPath);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{p.Store.DirectoryPath}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Error("Apertura cartella sessioni", ex);
            }
        }

        // ================= Analisi di una sessione =================

        private void HideAnalysis()
        {
            _analysisSeq++;
            _selected = null;
            AnalysisCard.Visibility = Visibility.Collapsed;
            SessionHistogram.Frametimes = null;
            SessionInsights.ItemsSource = null;
            CompareCombo.ItemsSource = null;
            CompareText.Text = "";
        }

        private async void ShowAnalysis(PerfSession s, bool reload)
        {
            _selected = s;
            AnalysisCard.Visibility = Visibility.Visible;
            AnalysisTitle.Text = s.Title;
            AnalysisMeta.Text = Meta(s);
            var st = s.Stats ?? new FrameStatsResult();
            StatsGrid.ItemsSource = BuildStats(st, s);

            // Grafici dai campioni al secondo (già nei metadati: nessun caricamento).
            var secs = s.Seconds ?? new List<SecondSample>();
            SessionFpsChart.Values = secs.Select(x => x.Fps).ToArray();
            SessionFpsChart.Values2 = secs.Select(x => x.Low1Fps > 0 ? x.Low1Fps : double.NaN).ToArray();
            SessionFpsChart.XLabel = $"tempo · {s.DurationText}";
            if (s.FpsCap is { } cap && cap > 0)
            {
                SessionFpsChart.ReferenceValue = cap;
                SessionFpsChart.ReferenceLabel = $"limite {N0(cap)} FPS";
            }
            else if (s.RefreshHz is { } hz && hz > 1)
            {
                SessionFpsChart.ReferenceValue = hz;
                SessionFpsChart.ReferenceLabel = $"monitor {hz} Hz";
            }
            else
            {
                SessionFpsChart.ReferenceValue = double.NaN;
                SessionFpsChart.ReferenceLabel = null;
            }
            SessionLoadChart.Values = secs.Select(x => x.CpuPercent).ToArray();
            SessionLoadChart.Values2 = secs.Any(x => x.GpuPercent.HasValue)
                ? secs.Select(x => x.GpuPercent ?? double.NaN).ToArray()
                : null;
            SessionLoadChart.XLabel = secs.Any(x => x.GpuPercent.HasValue) ? null : "GPU non disponibile";

            // Confronto: proposta automatica = sessione precedente dello stesso gioco.
            var others = _sessions.Where(x => x.Id != s.Id).ToList();
            CompareCombo.ItemsSource = others;
            CompareCombo.SelectedItem = others
                .Where(x => x.StartedAt < s.StartedAt && string.Equals(x.ProcessName, s.ProcessName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.StartedAt)
                .FirstOrDefault();
            UpdateCompare();

            if (!reload) return;

            // Frametime completi + analisi: possono essere milioni di valori, quindi in background.
            int seq = ++_analysisSeq;
            SessionHistogram.Frametimes = null;
            HistogramStatus.Text = "Caricamento dei frametime…";
            SessionInsights.ItemsSource = null;
            InsightsLoading.Visibility = Visibility.Visible;
            var p = P;
            if (p == null) return;
            var store = p.Store;
            var history = _sessions.ToList();
            try
            {
                var (frametimes, insights) = await Task.Run(() =>
                {
                    var ft = store.LoadFrametimes(s.Id);
                    var ins = PerfAnalyzer.Analyze(s, history, ft);
                    return (ft, ins);
                });
                if (seq != _analysisSeq) return; // nel frattempo è stata scelta un'altra sessione
                SessionHistogram.Frametimes = frametimes;
                HistogramStatus.Text = frametimes == null
                    ? "Frametime non disponibili per questa sessione"
                    : $"{frametimes.Length.ToString("N0", C)} frame";
                SessionInsights.ItemsSource = insights.Select(InsightRow.From).ToList();
            }
            catch (Exception ex)
            {
                Log.Error("Analisi sessione", ex);
                if (seq == _analysisSeq) HistogramStatus.Text = "Analisi non riuscita: " + ex.Message;
            }
            finally
            {
                if (seq == _analysisSeq) InsightsLoading.Visibility = Visibility.Collapsed;
            }
        }

        private static string Meta(PerfSession s)
        {
            var parts = new List<string>
            {
                s.StartedAt.ToString("dddd d MMMM yyyy, HH:mm", C),
                $"durata {s.DurationText}",
                DisplayName(s.ProcessName)
            };
            if (!string.IsNullOrEmpty(s.RenderMode)) parts.Add($"rendering {s.RenderMode}");
            if (s.FpsCap is { } cap) parts.Add(cap > 0 ? $"limite {N0(cap)} FPS" : "FPS illimitati");
            if (s.RefreshHz is { } hz && hz > 1) parts.Add($"monitor {hz} Hz");
            if (s.VramTotalGb is { } vram && vram > 0) parts.Add($"VRAM {vram.ToString("0", C)} GB");
            int tweaks = s.ActiveTweaks?.Count ?? 0;
            parts.Add(tweaks == 0 ? "nessun tweak FN Boost attivo" : tweaks == 1 ? "1 tweak FN Boost attivo" : $"{tweaks} tweak FN Boost attivi");
            return string.Join(" · ", parts);
        }

        private static List<StatItem> BuildStats(FrameStatsResult st, PerfSession s)
        {
            var text = Res("TextBrush");
            var accent = Res("Accent2Brush");
            string Ms(double v) => v > 0 ? v.ToString("0.00", C) + " ms" : "–";
            var cons = st.ConsistencyScore >= 80 ? Res("OkBrush") : st.ConsistencyScore >= 60 ? Res("WarnBrush") : Res("BadBrush");
            return new List<StatItem>
            {
                new() { Label = "FPS medi", Value = N0(st.AvgFps), Brush = accent,
                        Tip = "Frame totali divisi per il tempo: la media reale (non la media degli FPS istantanei)." },
                new() { Label = "1% low", Value = N0(st.Low1Fps), Brush = text,
                        Tip = "Media dell'1% dei frame più lenti, convertita in FPS. Indica la fluidità nei momenti peggiori: più è vicino alla media, meglio è." },
                new() { Label = "0,1% low", Value = N0(st.Low01Fps), Brush = text,
                        Tip = "Come l'1% low ma sullo 0,1% dei frame più lenti: cattura gli scatti rari e più evidenti." },
                new() { Label = "P1 (99° percentile)", Value = N0(st.P1Fps), Brush = text,
                        Tip = "FPS corrispondenti al 99° percentile dei frametime: il 99% dei frame è stato più veloce di così. Simile all'1% low ma meno sensibile ai singoli picchi." },
                new() { Label = "FPS minimi", Value = N0(st.MinFps), Brush = text,
                        Tip = "Calcolati dal frame più lento in assoluto (1000 / frametime massimo)." },
                new() { Label = "FPS massimi", Value = N0(st.MaxFps), Brush = text,
                        Tip = "Calcolati dal frame più veloce in assoluto (1000 / frametime minimo)." },
                new() { Label = "Consistenza", Value = st.HasData ? $"{st.ConsistencyScore.ToString("0", C)}/100" : "–", Brush = st.HasData ? cons : text,
                        Tip = "Punteggio 0-100 della regolarità dei frametime: 100 = perfettamente costanti. Conta molto per la sensazione di fluidità." },
                new() { Label = "Stutter", Value = st.HasData ? $"{st.Stutters} ({st.StuttersPerMin.ToString("0.0", C)}/min)" : "–", Brush = text,
                        Tip = "Frame che durano molto più dei vicini (soglia: «Sensibilità stutter»). Sotto 1 al minuto è ottimo." },
                new() { Label = "Frametime medio", Value = Ms(st.AvgFrametimeMs), Brush = text,
                        Tip = "Tempo medio di un frame. 16,7 ms = 60 FPS, 6,9 ms = 144 FPS, 4,2 ms = 240 FPS." },
                new() { Label = "Frametime mediano", Value = Ms(st.MedianFrametimeMs), Brush = text,
                        Tip = "Il frame «tipico»: metà dei frame è più veloce, metà più lenta." },
                new() { Label = "Frametime P99", Value = Ms(st.P99FrametimeMs), Brush = text,
                        Tip = "Il 99% dei frame dura meno di così." },
                new() { Label = "Frametime massimo", Value = Ms(st.MaxFrametimeMs), Brush = text,
                        Tip = "Il frame più lento della sessione (il «picco» peggiore)." },
                new() { Label = "Deviazione standard", Value = Ms(st.StdDevFrametimeMs), Brush = text,
                        Tip = "Quanto variano i frametime attorno alla media: più è bassa, più il ritmo è regolare." },
                new() { Label = "Frame", Value = st.Frames > 0 ? st.Frames.ToString("N0", C) : "–", Brush = text,
                        Tip = "Numero di frame misurati nella sessione." },
                new() { Label = "Durata", Value = s.DurationText, Brush = text,
                        Tip = "Durata della registrazione." },
                new() { Label = "Gioco", Value = DisplayName(s.ProcessName), Brush = text,
                        Tip = "Processo misurato (dall'elenco processi di Windows)." },
            };
        }

        private void CompareCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCompare();

        private void UpdateCompare()
        {
            if (_selected == null || CompareCombo.SelectedItem is not PerfSession baseline)
            {
                CompareText.Text = CompareCombo.Items.Count == 0 ? "Registra un'altra sessione per poter confrontare." : "";
                return;
            }
            try
            {
                CompareText.Text = PerfAnalyzer.Compare(_selected, baseline);
            }
            catch (Exception ex)
            {
                Log.Error("Confronto sessioni", ex);
                CompareText.Text = "";
            }
        }
    }
}
