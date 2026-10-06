using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using FNBoost.Core;

namespace FNBoost.Views.Pages
{
    public partial class CleanupPage : UserControl
    {
        private readonly List<CleanupTarget> _targets = Cleaner.CreateTargets();
        private bool _analyzed;

        public CleanupPage()
        {
            InitializeComponent();
            List.ItemsSource = _targets;
            Loaded += async (_, _) =>
            {
                if (!_analyzed) await AnalyzeAsync();
            };
        }

        private async Task AnalyzeAsync()
        {
            Busy(true);
            ResultText.Text = "Analisi…";
            try
            {
                await Task.Run(() =>
                {
                    foreach (var t in _targets)
                    {
                        try { Cleaner.Analyze(t, CancellationToken.None); }
                        catch (Exception ex) { Log.Warn($"Analisi '{t.Name}': {ex.Message}"); }
                    }
                });
                _analyzed = true;
                var total = _targets.Where(t => t.Selected && t.Bytes > 0).Sum(t => t.Bytes);
                ResultText.Text = $"Recuperabili con la selezione attuale: {Cleaner.FormatBytes(total)}";
            }
            finally
            {
                Busy(false);
            }
        }

        private async void Analyze_Click(object sender, RoutedEventArgs e) => await AnalyzeAsync();

        private async void Clean_Click(object sender, RoutedEventArgs e)
        {
            var selected = _targets.Where(t => t.Selected).ToList();
            if (selected.Count == 0) return;
            if (selected.Any(t => t.HasWarning))
            {
                var r = MessageBox.Show(Window.GetWindow(this)!,
                    "Hai selezionato la cache shader: dopo la pulizia le prime partite ricompileranno gli shader e potrebbero scattare.\n\nContinuare?",
                    "FN Boost", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes) return;
            }

            Busy(true);
            long freed = 0;
            int deleted = 0, skipped = 0;
            var errors = new List<string>();
            try
            {
                await Task.Run(() =>
                {
                    foreach (var t in selected)
                    {
                        try
                        {
                            var (f, d, s) = Cleaner.Clean(t, CancellationToken.None);
                            freed += f;
                            deleted += d;
                            skipped += s;
                        }
                        catch (Exception ex)
                        {
                            errors.Add(ex.Message);
                        }
                    }
                });
            }
            finally
            {
                Busy(false);
            }
            await AnalyzeAsync();
            ResultText.Text = $"Liberati {Cleaner.FormatBytes(freed)} ({deleted} file, {skipped} in uso saltati).";
            if (errors.Count > 0)
                MessageBox.Show(Window.GetWindow(this)!, string.Join("\n", errors), "FN Boost", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Busy(bool busy)
        {
            AnalyzeBtn.IsEnabled = !busy;
            CleanBtn.IsEnabled = !busy;
        }
    }
}
