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

    /// <summary>Riga della tabella "Processi in background" di una sessione.</summary>
    public sealed class ProcessRow
    {
        public string Name { get; init; } = "";
        public string AvgCpu { get; init; } = "";
        public string MaxCpu { get; init; } = "";
        public string Ram { get; init; } = "";
        public Brush CpuBrush { get; init; } = Brushes.White;
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

        // Ping degli ultimi 120 s per il grafico dal vivo (un punto per ogni nuovo NetworkSnapshot, cioè al secondo).
        private const int NetHistorySeconds = 120;
        private readonly List<double> _pingHist = new();
        private readonly List<double> _gwHist = new();
        private NetworkSnapshot? _lastNet;
        private bool _netChartDirty;

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
            RegionCombo.ItemsSource = new[] { PingRegion.Auto, PingRegion.Europe, PingRegion.NaEast, PingRegion.NaCentral,
                    PingRegion.NaWest, PingRegion.Brazil, PingRegion.Asia, PingRegion.Oceania, PingRegion.MiddleEast }
                .Select(r => new PerfOption
                {
                    Label = r == PingRegion.Auto ? "Automatica (la più vicina)" : NetStats.RegionDisplayName(r),
                    Value = r
                })
                .ToArray();
            ReportExplain.Text = ReportActions.Explanation;
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
            // Riletti a ogni apertura: le scorciatoie si possono cambiare in Sicurezza e backup.
            OverlayHotkeyHint.Text = $"Scorciatoia globale: {App.Settings.HotkeyOverlay}";
            RecordHotkeyHint.Text = $"Scorciatoia per avviare/fermare una registrazione: {App.Settings.HotkeyRecord}";
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
            // La cronologia del ping si aggiorna anche a pagina nascosta, così il grafico è completo al ritorno.
            PushNetHistory(snap.Net);
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
            UpdateNet(snap.Net);
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
                NetToggle.IsChecked = s.NetCaptureEnabled;
                GatewayToggle.IsChecked = s.PingGateway;
                ProcToggle.IsChecked = s.TrackProcesses;
                RegionCombo.SelectedValue = s.Region;
                UpdateNetToggles();
                StutterSlider.Value = Math.Clamp(s.StutterFactor, StutterSlider.Minimum, StutterSlider.Maximum);
                StutterValue.Text = StutterSlider.Value.ToString("0.##", C) + "×";
            }
            finally
            {
                _loadingSettings = false;
            }
        }

        private void PerfSetting_Click(object sender, RoutedEventArgs e)
        {
            UpdateNetToggles();
            ScheduleApply();
        }

        /// <summary>Router e regione contano solo con la misura di rete accesa.</summary>
        private void UpdateNetToggles()
        {
            bool net = NetToggle.IsChecked == true;
            GatewayToggle.IsEnabled = net;
            RegionCombo.IsEnabled = net;
        }

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
            s.NetCaptureEnabled = NetToggle.IsChecked == true;
            s.PingGateway = GatewayToggle.IsChecked == true;
            s.TrackProcesses = ProcToggle.IsChecked == true;
            if (RegionCombo.SelectedValue is PingRegion region) s.Region = region;
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
                // Letto PRIMA di toccare la ComboBox: un nuovo ItemsSource azzera la selezione e il binding
                // TwoWay riscriverebbe "" nelle impostazioni (perdendo il monitor scelto).
                var keep = o.Monitor;
                var monitors = Monitors.GetAll();
                if (!Monitors.SameLayout(MonitorCombo.ItemsSource, monitors))
                    MonitorCombo.ItemsSource = monitors;
                var want = Monitors.Resolve(monitors, keep);
                if (o.Monitor != want) o.Monitor = want;
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
            SessionReportBtn.IsEnabled = sel && !ReportActions.IsBusy;
            UpdateReportHint();
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
            ShowSessionNetwork(s, secs);
            ShowSessionProcesses(s);

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

        // ================= Rete dal vivo =================

        private static string Ms(double? v) => v is { } x && double.IsFinite(x) ? x.ToString("0", C) + " ms" : "–";

        private static string Pct(double v) => v.ToString(v > 0 && v < 10 ? "0.#" : "0", C) + "%";

        /// <summary>kbit/s sotto 1000, poi Mbit/s con un decimale.</summary>
        private static string Kbps(double v) =>
            v >= 1000 ? (v / 1000).ToString("0.0", C) + " Mbit/s" : v.ToString("0", C) + " kbit/s";

        private static Brush LevelBrush(int level, string okKey = "TextBrush") =>
            Res(level >= 2 ? "BadBrush" : level == 1 ? "WarnBrush" : okKey);

        private static int MaxLevel(params int[] levels) => levels.Length == 0 ? 0 : levels.Max();

        /// <summary>Jitter e perdita di un bersaglio secondario (router, Internet): "jitter 1 · perdita 0%".</summary>
        private static string JitterLoss(PingStats p) =>
            $"jitter {(p.JitterMs is { } j ? j.ToString("0", C) : "–")} · perdita {(p.Sent > 0 ? Pct(p.LossPct) : "–")}";

        private void PushNetHistory(NetworkSnapshot? net)
        {
            if (ReferenceEquals(net, _lastNet)) return; // stesso secondo (i LiveSnapshot arrivano 4 volte al secondo)
            _lastNet = net;
            if (net == null)
            {
                if (_pingHist.Count > 0)
                {
                    _pingHist.Clear();
                    _gwHist.Clear();
                    _netChartDirty = true;
                }
                return;
            }
            _pingHist.Add(net.Game?.LastMs ?? double.NaN);
            _gwHist.Add(net.Gateway?.LastMs ?? double.NaN);
            if (_pingHist.Count > NetHistorySeconds) _pingHist.RemoveAt(0);
            if (_gwHist.Count > NetHistorySeconds) _gwHist.RemoveAt(0);
            _netChartDirty = true;
        }

        private void UpdateNet(NetworkSnapshot? net)
        {
            if (_netChartDirty)
            {
                _netChartDirty = false;
                NetChart.Values = _pingHist.ToArray();
                NetChart.Values2 = _gwHist.Any(double.IsFinite) ? _gwHist.ToArray() : null;
            }

            if (net == null || !net.Available)
            {
                NetPanel.Visibility = Visibility.Collapsed;
                NetOffText.Visibility = Visibility.Visible;
                NetDot.Fill = Res("MutedBrush");
                NetStatusText.Text = "";
                var s = App.Settings.Perf;
                NetOffText.Text = net != null
                    ? "Misura di rete non disponibile: " + (string.IsNullOrEmpty(net.StatusText) ? "avvio in corso…" : net.StatusText)
                    : !s.NetCaptureEnabled
                        ? "Misura di rete spenta. Attivala in Impostazioni › Misurazione › «Misura ping e rete» per vedere ping, jitter, perdita di pacchetti e traffico del gioco."
                        : !s.CaptureEnabled
                            ? "La misura di rete lavora insieme al contatore: attiva «Misura gli FPS in background» qui sotto."
                            : "Avvio della misura di rete…";
                return;
            }

            NetPanel.Visibility = Visibility.Visible;
            NetOffText.Visibility = Visibility.Collapsed;
            NetStatusText.Text = net.StatusText;
            NetStatusText.ToolTip = string.IsNullOrEmpty(net.StatusText) ? null : net.StatusText;

            // ---- ping di gioco ----
            var g = net.Game;
            double? ping = NetDisplay.PingMs(g);
            bool hasPing = ping.HasValue;
            NetDot.Fill = hasPing ? LevelBrush(NetDisplay.Level(g), "OkBrush") : Res("WarnBrush");
            NetPingText.Text = Ms(ping);
            NetPingText.Foreground = LevelBrush(NetDisplay.PingLevel(ping));
            NetPingSub.Text = !hasPing
                ? "in attesa del primo ping"
                : net.PingTargetKind == "server"
                    ? "verso il server di gioco"
                    : !string.IsNullOrEmpty(net.RegionName) ? $"verso la regione {net.RegionName}" : "verso la regione Epic";
            NetJitterText.Text = Ms(g?.JitterMs);
            NetJitterText.Foreground = LevelBrush(NetDisplay.JitterLevel(g?.JitterMs));
            if (g != null && g.Sent > 0)
            {
                NetLossText.Text = Pct(g.LossPct);
                NetLossText.Foreground = LevelBrush(NetDisplay.LossLevel(g.LossPct));
                int lost = Math.Max(0, g.Sent - g.Received);
                NetLossSub.Text = $"{lost} persi su {g.Sent} · ultimo minuto";
            }
            else
            {
                NetLossText.Text = "–";
                NetLossText.Foreground = Res("TextBrush");
                NetLossSub.Text = "ultimo minuto";
            }

            // ---- router e Internet ----
            var gw = net.Gateway;
            if (gw != null && gw.AvgMs.HasValue)
            {
                NetGwText.Text = Ms(gw.AvgMs);
                NetGwText.Foreground = LevelBrush(MaxLevel(NetDisplay.JitterLevel(gw.JitterMs), NetDisplay.LossLevel(gw.Sent > 0 ? gw.LossPct : null)));
                NetGwSub.Text = JitterLoss(gw);
            }
            else
            {
                NetGwText.Text = "–";
                NetGwText.Foreground = Res("TextBrush");
                NetGwSub.Text = App.Settings.Perf.PingGateway ? (gw != null && gw.Sent > 0 ? "il router non risponde al ping" : "in attesa") : "disattivato";
            }
            var inet = net.Internet;
            if (inet != null && inet.AvgMs.HasValue)
            {
                NetInetText.Text = Ms(inet.AvgMs);
                NetInetText.Foreground = LevelBrush(MaxLevel(NetDisplay.JitterLevel(inet.JitterMs), NetDisplay.LossLevel(inet.Sent > 0 ? inet.LossPct : null)));
                NetInetSub.Text = JitterLoss(inet);
            }
            else
            {
                NetInetText.Text = "–";
                NetInetText.Foreground = Res("TextBrush");
                NetInetSub.Text = inet != null && inet.Sent > 0 ? "nessuna risposta" : "in attesa";
            }

            // ---- regione Epic ----
            NetRegionText.Text = Ms(net.Region?.AvgMs);
            var regionParts = new List<string>();
            if (!string.IsNullOrEmpty(net.RegionName)) regionParts.Add(net.RegionName!);
            if (!string.IsNullOrEmpty(net.BestRegionName))
            {
                regionParts.Add(string.Equals(net.BestRegionName, net.RegionName, StringComparison.OrdinalIgnoreCase)
                    ? "la più vicina"
                    : $"migliore: {net.BestRegionName}" + (net.BestRegionPingMs is { } b ? $" {b.ToString("0", C)} ms" : ""));
            }
            NetRegionSub.Text = regionParts.Count > 0 ? string.Join(" · ", regionParts) : "scansione in corso";

            // ---- traffico del gioco (serve la traccia ETW, quindi l'amministratore) ----
            bool traffic = net.TrafficAvailable;
            bool server = traffic && !string.IsNullOrEmpty(net.ServerEndpoint);
            const string notMeasured = "non misurato (vedi stato)";
            NetPpsText.Text = server ? $"{net.PacketsInPerSec.ToString("0", C)} / {net.PacketsOutPerSec.ToString("0", C)}" : "–";
            NetPpsSub.Text = traffic ? "ricevuti / inviati" : notMeasured;
            NetBwText.Text = server ? $"{net.GameKbpsIn.ToString("0", C)} / {net.GameKbpsOut.ToString("0", C)}" : "–";
            NetBwSub.Text = traffic ? "kbit/s · download / upload" : notMeasured;
            if (traffic)
            {
                NetOtherText.Text = Kbps(net.OtherAppsKbps);
                NetOtherText.Foreground = LevelBrush(net.OtherAppsKbps > 10000 ? 2 : net.OtherAppsKbps > 2000 ? 1 : 0);
                NetOtherSub.Text = $"totale PC {Kbps(net.TotalKbpsIn + net.TotalKbpsOut)}";
                NetFreezeText.Text = net.RecentFreezes.ToString(C);
                NetFreezeText.Foreground = LevelBrush(net.RecentFreezes >= 3 ? 2 : net.RecentFreezes >= 1 ? 1 : 0);
                NetFreezeSub.Text = server && net.MaxRecvGapMs > 0
                    ? $"ultimi 60 s · pausa max {net.MaxRecvGapMs.ToString("0", C)} ms"
                    : "ultimi 60 s";
            }
            else
            {
                NetOtherText.Text = "–";
                NetOtherText.Foreground = Res("TextBrush");
                NetOtherSub.Text = notMeasured;
                NetFreezeText.Text = "–";
                NetFreezeText.Foreground = Res("TextBrush");
                NetFreezeSub.Text = notMeasured;
            }

            // ---- connessione ----
            NetConnText.Text = string.IsNullOrEmpty(net.ConnectionType) ? "–" : net.ConnectionType;
            var conn = new List<string>();
            if (net.WifiSignalPct is { } sig) conn.Add($"segnale {sig}%");
            if (net.LinkSpeedMbps is { } link && link > 0)
                conn.Add(link >= 1000 ? $"{(link / 1000).ToString("0.#", C)} Gbit/s" : $"{link.ToString("0", C)} Mbit/s");
            NetConnSub.Text = conn.Count > 0 ? string.Join(" · ", conn) : (string.IsNullOrEmpty(net.AdapterName) ? "–" : net.AdapterName);
            NetConnText.Foreground = LevelBrush(net.WifiSignalPct is { } w ? (w < 40 ? 2 : w < 60 ? 1 : 0) : 0);
            NetConnText.ToolTip = string.IsNullOrEmpty(net.AdapterName) ? null : net.AdapterName;

            // ---- server ----
            NetServerText.Text = string.IsNullOrEmpty(net.ServerEndpoint) ? "–" : net.ServerEndpoint;
            NetServerSub.Text = string.IsNullOrEmpty(net.ServerEndpoint)
                ? (traffic ? "in attesa della partita" : notMeasured)
                : net.PingTargetKind == "server" ? "risponde al ping" : "non risponde al ping: si usa la regione";
        }

        // ================= Report diagnostico =================

        /// <summary>La sessione selezionata nello storico, altrimenti null (= ReportService usa l'ultima salvata).</summary>
        private PerfSession? ReportSession => (SessionList.SelectedItem as SessionRow)?.Session;

        private void UpdateReportHint()
        {
            if (ReportActions.IsBusy) return;
            var s = ReportSession;
            ReportHint.Text = s != null
                ? $"Sessione inclusa: {s.Title} ({s.DurationText}), selezionata nello storico."
                : _sessions.Count > 0
                    ? "Sessione inclusa: l'ultima salvata (selezionane un'altra nello storico per cambiarla)."
                    : "Nessuna sessione salvata: il report conterrà sistema, rete dal vivo, tweak e log. Registra una partita per avere anche gli FPS.";
        }

        private void SetReportBusy(bool busy, string? text = null)
        {
            ReportExportBtn.IsEnabled = !busy;
            ReportCopyBtn.IsEnabled = !busy;
            SessionReportBtn.IsEnabled = !busy && SessionList.SelectedItem is SessionRow;
            ReportExportText.Text = busy && text != null ? text : "Esporta report completo (.zip)";
            Cursor = busy ? Cursors.AppStarting : null;
            if (busy) ReportHint.Text = "Raccolta dei dati e pulizia dei dati personali: qualche secondo…";
        }

        private async void ReportExport_Click(object sender, RoutedEventArgs e)
        {
            if (ReportActions.IsBusy) return;
            // Il pulsante dello storico usa sempre la sessione selezionata; quello in alto la selezionata o l'ultima.
            var session = ReportSession;
            SetReportBusy(true, "Creazione del report…");
            string? path = null;
            try
            {
                path = await ReportActions.ExportAsync(session, Window.GetWindow(this));
            }
            finally
            {
                SetReportBusy(false);
                UpdateReportHint();
            }
            if (path != null) ReportHint.Text = $"Report salvato: {path}";
        }

        private async void ReportCopy_Click(object sender, RoutedEventArgs e)
        {
            if (ReportActions.IsBusy) return;
            SetReportBusy(true);
            ReportCopyText.Text = "Preparazione…";
            int? chars = null;
            try
            {
                chars = await ReportActions.CopySummaryAsync(ReportSession, Window.GetWindow(this));
            }
            finally
            {
                SetReportBusy(false);
                ReportCopyText.Text = "Copia riepilogo per la chat";
                UpdateReportHint();
            }
            if (chars is { } n)
                ReportHint.Text = $"Riepilogo copiato negli appunti ({n.ToString("N0", C)} caratteri): incollalo con Ctrl+V nella chat. I dati personali sono già stati rimossi.";
        }

        private void ReportFolder_Click(object sender, RoutedEventArgs e) => ReportActions.OpenFolder();

        // ================= Rete e processi di una sessione =================

        private void ShowSessionNetwork(PerfSession s, List<SecondSample> secs)
        {
            var n = s.Network;
            bool anyPing = secs.Any(x => x.PingMs.HasValue);
            if (n == null && !anyPing)
            {
                SessionNetPanel.Visibility = Visibility.Collapsed;
                SessionPingChart.Values = null;
                SessionPingChart.Values2 = null;
                SessionLossChart.Values = null;
                NetStatsGrid.ItemsSource = null;
                return;
            }
            SessionNetPanel.Visibility = Visibility.Visible;

            SessionPingChart.Values = secs.Select(x => x.PingMs ?? double.NaN).ToArray();
            SessionPingChart.Values2 = secs.Any(x => x.GatewayPingMs.HasValue)
                ? secs.Select(x => x.GatewayPingMs ?? double.NaN).ToArray()
                : null;
            SessionPingChart.XLabel = $"tempo · {s.DurationText} · ms";
            if (n?.BestRegionPingMs is { } best && !string.IsNullOrEmpty(n.BestRegionName))
            {
                SessionPingChart.ReferenceValue = best;
                SessionPingChart.ReferenceLabel = $"regione migliore ({n.BestRegionName}) {best.ToString("0", C)} ms";
            }
            else
            {
                SessionPingChart.ReferenceValue = double.NaN;
                SessionPingChart.ReferenceLabel = null;
            }
            SessionLossChart.Values = secs.Any(x => x.LossPct.HasValue) ? secs.Select(x => x.LossPct ?? double.NaN).ToArray() : null;
            SessionLossChart.XLabel = "tempo";

            SessionNetTitle.Text = n == null ? "Rete (riepilogo non disponibile per questa sessione)" : "Rete";
            NetStatsGrid.ItemsSource = n != null ? BuildNetStats(n) : null;
        }

        private static List<StatItem> BuildNetStats(NetworkSummary n)
        {
            var text = Res("TextBrush");
            var g = n.Game;
            var servers = n.ServerEndpoints ?? new List<string>();
            string serverText = servers.Count == 0 ? "non rilevato"
                : servers.Count == 1 ? servers[0]
                : $"{servers[0]} +{servers.Count - 1}";
            string target = n.PingTargetKind == "server" ? "server di gioco"
                : n.PingTargetKind == "regione" ? $"regione {n.RegionName ?? "Epic"}" : "–";
            string conn = string.IsNullOrEmpty(n.ConnectionType) ? "–" : n.ConnectionType;
            if (n.WifiSignalPct is { } sig) conn += $" · {sig}%";
            string Sec(PingStats? p) => p?.AvgMs is { } a ? $"{Ms(a)} · jitter {(p.JitterMs is { } j ? j.ToString("0", C) : "–")}" : "–";
            int pingLvl = NetDisplay.PingLevel(g?.AvgMs);

            return new List<StatItem>
            {
                new() { Label = "Ping medio", Value = Ms(g?.AvgMs), Brush = pingLvl > 0 ? LevelBrush(pingLvl) : Res("Accent2Brush"),
                        Tip = "Media dei ping ICMP verso " + target + ". Sotto 50 ms è ottimo, oltre 80 ms si inizia a sentire." },
                new() { Label = "Ping P95", Value = Ms(g?.P95Ms), Brush = LevelBrush(NetDisplay.PingLevel(g?.P95Ms)),
                        Tip = "Il 95% dei ping è stato più veloce di così: mostra i picchi ricorrenti." },
                new() { Label = "Ping massimo", Value = Ms(g?.MaxMs), Brush = text,
                        Tip = "Il ping più alto della sessione (un singolo picco conta poco)." },
                new() { Label = "Jitter", Value = Ms(g?.JitterMs), Brush = LevelBrush(NetDisplay.JitterLevel(g?.JitterMs)),
                        Tip = "Variazione media tra ping consecutivi. Sotto 10 ms è buono, oltre 25 ms il gioco può sembrare a scatti." },
                new() { Label = "Perdita pacchetti", Value = g != null && g.Sent > 0 ? Pct(g.LossPct) : "–",
                        Brush = LevelBrush(NetDisplay.LossLevel(g != null && g.Sent > 0 ? g.LossPct : null)),
                        Tip = g != null && g.Sent > 0 ? $"{Math.Max(0, g.Sent - g.Received)} ping senza risposta su {g.Sent}." : "Nessun ping inviato." },
                new() { Label = "Ping misurato verso", Value = target, Brush = text,
                        Tip = "Molti server di gioco non rispondono al ping: in quel caso si misura l'endpoint ufficiale Epic della regione." },
                new() { Label = "Server di gioco", Value = serverText, Brush = text,
                        Tip = servers.Count == 0 ? "Nessun traffico del gioco rilevato (serve l'amministratore per la traccia di rete)." : string.Join("\n", servers) },
                new() { Label = "Regione Epic", Value = n.RegionName ?? "–", Brush = text,
                        Tip = n.Region?.AvgMs is { } ra ? $"Ping medio verso la regione: {Ms(ra)}." : "Regione usata come riferimento per il ping." },
                new() { Label = "Regione migliore", Value = n.BestRegionName is { } bn ? $"{bn} {Ms(n.BestRegionPingMs)}" : "–", Brush = text,
                        Tip = "La regione Epic con il ping più basso: se è molto sotto il ping di gioco, stai giocando su server lontani (controlla la regione di matchmaking in Fortnite)." },
                new() { Label = "Router", Value = Sec(n.Gateway), Brush = LevelBrush(NetDisplay.JitterLevel(n.Gateway?.JitterMs)),
                        Tip = "Ping verso il router di casa: se è instabile il problema è la rete locale (spesso il Wi-Fi)." },
                new() { Label = "Internet (1.1.1.1)", Value = Sec(n.Internet), Brush = text,
                        Tip = "Ping verso 1.1.1.1 come riferimento della linea Internet." },
                new() { Label = "Connessione", Value = conn, Brush = text,
                        Tip = n.LinkSpeedMbps is { } l && l > 0 ? $"Velocità del collegamento: {l.ToString("0", C)} Mbit/s." : "Tipo di connessione usata per Internet." },
                new() { Label = "Pacchetti/s (medi)", Value = n.AvgPacketsInPerSec > 0 || n.AvgPacketsOutPerSec > 0
                            ? $"{n.AvgPacketsInPerSec.ToString("0", C)} / {n.AvgPacketsOutPerSec.ToString("0", C)}" : "–", Brush = text,
                        Tip = "Pacchetti UDP al secondo ricevuti dal server / inviati al server." },
                new() { Label = "Banda gioco (media)", Value = n.AvgGameKbpsIn > 0 || n.AvgGameKbpsOut > 0
                            ? $"{n.AvgGameKbpsIn.ToString("0", C)} / {n.AvgGameKbpsOut.ToString("0", C)} kbit/s" : "–", Brush = text,
                        Tip = "Download / upload del gioco: Fortnite usa poca banda, conta di più la stabilità." },
                new() { Label = "Altre app (media / max)", Value = $"{Kbps(n.AvgOtherAppsKbps)} / {Kbps(n.MaxOtherAppsKbps)}",
                        Brush = LevelBrush(n.AvgOtherAppsKbps > 2000 || n.MaxOtherAppsKbps > 10000 ? 1 : 0),
                        Tip = "Traffico delle altre app di questo PC durante la sessione (download, aggiornamenti, streaming)." },
                new() { Label = "Freeze di rete", Value = n.Freezes > 0 ? $"{n.Freezes} (max {n.LongestFreezeMs.ToString("0", C)} ms)" : "0",
                        Brush = LevelBrush(n.Freezes >= 5 ? 2 : n.Freezes >= 1 ? 1 : 0),
                        Tip = "Volte in cui il server non ha mandato pacchetti per oltre 250 ms: in gioco è lag, non uno stutter degli FPS." },
            };
        }

        private void ShowSessionProcesses(PerfSession s)
        {
            var list = s.TopProcesses ?? new List<ProcessUsage>();
            if (list.Count == 0)
            {
                ProcessPanel.Visibility = Visibility.Collapsed;
                ProcessList.ItemsSource = null;
                return;
            }
            string P1(double v) => v.ToString("0.0", C) + "%";
            ProcessList.ItemsSource = list.Take(10).Select(p => new ProcessRow
            {
                Name = p.Name,
                AvgCpu = P1(p.AvgCpuPct),
                MaxCpu = P1(p.MaxCpuPct),
                Ram = p.AvgRamMb >= 1024 ? (p.AvgRamMb / 1024).ToString("0.0", C) + " GB" : p.AvgRamMb.ToString("0", C) + " MB",
                CpuBrush = LevelBrush(p.AvgCpuPct >= 15 ? 2 : p.AvgCpuPct >= 5 ? 1 : 0)
            }).ToList();
            ProcessPanel.Visibility = Visibility.Visible;
        }
    }
}
