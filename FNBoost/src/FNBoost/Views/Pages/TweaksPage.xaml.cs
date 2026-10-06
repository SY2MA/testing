using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using FNBoost.Core;

namespace FNBoost.Views.Pages
{
    public partial class TweaksPage : UserControl
    {
        public TweaksPage()
        {
            InitializeComponent();
            var view = new CollectionViewSource { Source = App.Tweaks.Tweaks };
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Tweak.Category)));
            List.ItemsSource = view.View;
        }

        private async void Toggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox cb || cb.DataContext is not Tweak t) return;
            var wantOn = cb.IsChecked == true;
            var ok = await App.Tweaks.SetAsync(t, wantOn);
            if (!ok)
                MessageBox.Show(Window.GetWindow(this)!, $"'{t.Title}' non è stato modificato.\nDettagli nel log (Sicurezza e backup).",
                    "FN Boost", MessageBoxButton.OK, MessageBoxImage.Warning);
            else if (t.RequiresReboot)
                Log.Info($"'{t.Title}': riavvia il PC per rendere effettiva la modifica.");
        }

        private async void Apply_Click(object sender, RoutedEventArgs e)
        {
            Enable(false);
            try { await App.Tweaks.ApplyRecommendedAsync(); }
            finally { Enable(true); }
        }

        private async void Revert_Click(object sender, RoutedEventArgs e)
        {
            var r = MessageBox.Show(Window.GetWindow(this)!, "Ripristinare tutte le modifiche fatte da FN Boost?",
                "FN Boost", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
            Enable(false);
            try { await App.Tweaks.RevertAllAsync(); }
            finally { Enable(true); }
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            Enable(false);
            try { await App.Tweaks.RefreshAsync(); }
            finally { Enable(true); }
        }

        private void Enable(bool on)
        {
            ApplyBtn.IsEnabled = on;
            RevertBtn.IsEnabled = on;
            RefreshBtn.IsEnabled = on;
        }
    }
}
