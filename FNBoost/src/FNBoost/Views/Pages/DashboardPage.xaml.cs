using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using FNBoost.Core;
using FNBoost.Perf;

namespace FNBoost.Views.Pages
{
    public partial class DashboardPage : UserControl
    {
        private bool _busy;

        public DashboardPage()
        {
            InitializeComponent();
            Loaded += async (_, _) =>
            {
                if (ChecksList.ItemsSource == null) await AnalyzeAsync();
                await LoadLastSessionAsync();
            };
            App.Tweaks.Changed += UpdateScore;
            UpdateScore();
            ReportExplain.Text = ReportActions.Explanation;
        }

        private void UpdateScore()
        {
            ScoreText.Text = $"{App.Tweaks.RecommendedActive}/{App.Tweaks.RecommendedCount}";
        }

        private async Task AnalyzeAsync()
        {
            if (_busy) return;
            _busy = true;
            SetButtons(false);
            LoadingText.Visibility = Visibility.Visible;
            try
            {
                await App.Tweaks.RefreshAsync();
                var tweaks = App.Tweaks.Tweaks.ToList();
                var s = await Task.Run(() => SystemDiagnostics.Collect(tweaks));

                OsText.Text = s.Os;
                BoardText.Text = s.BoardText;
                CpuText.Text = s.Cpu;
                CpuDetail.Text = s.CpuDetailText;
                GpuText.Text = s.GpuText;
                GpuDetail.Text = s.GpuDriverText;
                RamText.Text = s.RamText;
                RamDetail.Text = s.RamDetailText;
                DisplayText.Text = s.DisplayText;
                FnText.Text = s.FortniteDir != null ? "Installato" : "Non trovato";
                FnDetail.Text = s.FortniteDir != null
                    ? $"{s.FortniteDir}{(s.FortniteDiskType != null ? $"\n{s.FortniteDiskType}" : "")}{(s.FortniteDriveFreeGb is { } gb ? $" · {gb:0} GB liberi" : "")}"
                    : "Installa Fortnite dall'Epic Games Launcher";

                ChecksList.ItemsSource = s.Checks
                    .OrderBy(c => c.Status switch { CheckStatus.Bad => 0, CheckStatus.Warn => 1, CheckStatus.Info => 2, _ => 3 })
                    .ToList();
                var issues = s.Checks.Count(c => c.Status is CheckStatus.Bad or CheckStatus.Warn);
                Log.Info(issues == 0 ? "Analisi completata: nessun problema importante." : $"Analisi completata: {issues} punti da sistemare.");
            }
            catch (Exception ex)
            {
                Log.Error("Analisi", ex);
            }
            finally
            {
                LoadingText.Visibility = Visibility.Collapsed;
                SetButtons(true);
                _busy = false;
            }
        }

        /// <summary>Riepilogo dell'ultima sessione registrata (se ce ne sono), con il confronto con la precedente dello stesso gioco.</summary>
        private async Task LoadLastSessionAsync()
        {
            var perf = App.Perf;
            if (perf == null)
            {
                LastSessionCard.Visibility = Visibility.Collapsed;
                return;
            }
            try
            {
                // La prima lettura dell'archivio legge i file dal disco: fuori dal thread della UI.
                IReadOnlyList<PerfSession> list = await Task.Run(() => perf.Store.List());
                var last = list.FirstOrDefault(x => x.Stats != null && x.Stats.HasData);
                if (last == null)
                {
                    LastSessionCard.Visibility = Visibility.Collapsed;
                    return;
                }
                var c = CultureInfo.CurrentCulture;
                // Solo partita se la sessione ce l'ha: lobby e caricamenti abbassano media e low senza dire nulla del PC.
                var st = last.HeadlineStats;
                var game = string.Equals(last.ProcessName, FortniteLocator.ClientProcessName, StringComparison.OrdinalIgnoreCase)
                    ? "Fortnite"
                    : string.IsNullOrEmpty(last.ProcessName) ? "gioco" : last.ProcessName;
                LastSessionTitle.Text = $"Ultima sessione registrata · {game} · {last.Title}";
                LastSessionText.Text = (last.HeadlineIsMatch ? "Solo partita: media " : "Media ") +
                                       $"{st.AvgFps.ToString("0", c)} FPS · 1% low {st.Low1Fps.ToString("0", c)} FPS · " +
                                       $"{st.StuttersPerMin.ToString("0.0", c)} stutter/min · durata {last.DurationText}" +
                                       (last.HeadlineIsMatch && last.Stats is { HasData: true } whole
                                           ? $" (sessione intera, incluse lobby e caricamenti: {whole.AvgFps.ToString("0", c)} / {whole.Low1Fps.ToString("0", c)})"
                                           : "");
                var prev = list.FirstOrDefault(x => !ReferenceEquals(x, last) && x.Stats != null && x.Stats.HasData &&
                                                    string.Equals(x.ProcessName, last.ProcessName, StringComparison.OrdinalIgnoreCase));
                var cmp = prev != null ? PerfAnalyzer.Compare(last, prev) : "";
                LastSessionCompare.Text = cmp;
                LastSessionCompare.Visibility = string.IsNullOrEmpty(cmp) ? Visibility.Collapsed : Visibility.Visible;
                LastSessionCard.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                Log.Warn("Riepilogo ultima sessione: " + ex.Message);
                LastSessionCard.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>Stesso flusso della pagina Prestazioni: ZIP con l'ultima sessione salvata, poi Esplora file sul file.</summary>
        private async void Report_Click(object sender, RoutedEventArgs e)
        {
            if (ReportActions.IsBusy) return;
            ReportBtn.IsEnabled = false;
            ReportBtnText.Text = "Creazione del report…";
            ReportHint.Text = "Raccolta dei dati e pulizia dei dati personali: qualche secondo…";
            string? path = null;
            try
            {
                path = await ReportActions.ExportAsync(null, Window.GetWindow(this));
            }
            finally
            {
                ReportBtn.IsEnabled = true;
                ReportBtnText.Text = "Crea report diagnostico";
            }
            ReportHint.Text = path != null
                ? $"Report salvato: {path}"
                : "Include l'ultima sessione registrata. Per sceglierne un'altra usa la pagina Prestazioni.";
        }

        private void OpenPerformance_Click(object sender, RoutedEventArgs e)
        {
            if (Window.GetWindow(this) is MainWindow main) main.Navigate("performance");
        }

        private void SetButtons(bool enabled)
        {
            ApplyBtn.IsEnabled = enabled;
            RevertBtn.IsEnabled = enabled;
            RefreshBtn.IsEnabled = enabled;
        }

        private async void Apply_Click(object sender, RoutedEventArgs e)
        {
            var r = MessageBox.Show(Window.GetWindow(this)!,
                "Verranno applicati solo i tweak contrassegnati come 'Consigliato'.\n\n" +
                "• Prima viene creato un punto di ripristino di Windows\n" +
                "• Ogni valore originale viene salvato e può essere ripristinato\n" +
                "• Alcune modifiche richiedono un riavvio\n\nContinuare?",
                "Applica profilo consigliato", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
            SetButtons(false);
            try
            {
                var (ok, fail) = await App.Tweaks.ApplyRecommendedAsync();
                var msg = $"Applicati {ok} tweak{(fail > 0 ? $", {fail} non riusciti (vedi log)" : "")}.";
                if (App.Tweaks.RebootPending) msg += "\n\nRiavvia il PC per completare (HAGS / file di paging).";
                MessageBox.Show(Window.GetWindow(this)!, msg, "FN Boost", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            finally
            {
                SetButtons(true);
            }
            await AnalyzeAsync();
        }

        private async void Revert_Click(object sender, RoutedEventArgs e)
        {
            var r = MessageBox.Show(Window.GetWindow(this)!,
                "Ripristinare tutte le modifiche fatte da FN Boost ai valori originali?",
                "Ripristina tutto", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
            SetButtons(false);
            try
            {
                var (ok, fail) = await App.Tweaks.RevertAllAsync();
                MessageBox.Show(Window.GetWindow(this)!, $"Ripristinati {ok} tweak{(fail > 0 ? $", {fail} errori (vedi log)" : "")}.",
                    "FN Boost", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            finally
            {
                SetButtons(true);
            }
            await AnalyzeAsync();
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e) => await AnalyzeAsync();
    }
}
