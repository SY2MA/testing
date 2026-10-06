using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using FNBoost.Core;

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
            };
            App.Tweaks.Changed += UpdateScore;
            UpdateScore();
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
