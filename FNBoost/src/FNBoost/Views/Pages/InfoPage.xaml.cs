using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace FNBoost.Views.Pages
{
    public partial class InfoPage : UserControl
    {
        public InfoPage()
        {
            InitializeComponent();
            VersionText.Text = $"Versione {App.Version}";
        }

        private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }
    }
}
