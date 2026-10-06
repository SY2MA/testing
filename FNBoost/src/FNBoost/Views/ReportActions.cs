using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using FNBoost.Core;
using FNBoost.Perf;
using FNBoost.Report;

namespace FNBoost.Views
{
    /// <summary>
    /// Azioni del report diagnostico condivise da Prestazioni e Panoramica: esporta lo ZIP, copia il riepilogo,
    /// apre la cartella. Vanno chiamate dal thread della UI (ReportService legge lì lo stato dal vivo).
    /// </summary>
    public static class ReportActions
    {
        /// <summary>Una frase per spiegare all'utente cosa contiene il report.</summary>
        public const string Explanation =
            "Il report raccoglie in un file ZIP statistiche FPS, frametime, rete e ping, processi in background, tweak attivi e gli " +
            "eventi utili del log di Fortnite, con consigli; nome utente, nome del PC, ID Epic, e-mail e IP personali vengono rimossi prima del salvataggio.";

        private static int _busy;

        /// <summary>Vero mentre un'esportazione o un riepilogo è in corso (per non lanciarne due insieme).</summary>
        public static bool IsBusy => Volatile.Read(ref _busy) != 0;

        /// <summary>
        /// Crea lo ZIP (sessione indicata, altrimenti l'ultima salvata), poi mostra il percorso e apre Esplora file
        /// con il file selezionato. Restituisce il percorso o null se non è riuscito (l'errore è già stato mostrato).
        /// </summary>
        public static async Task<string?> ExportAsync(PerfSession? session, Window? owner)
        {
            if (Interlocked.Exchange(ref _busy, 1) != 0) return null;
            string path;
            try
            {
                path = await ReportService.ExportZipAsync(session);
            }
            catch (Exception ex)
            {
                // ReportService ha già scritto l'errore nel registro.
                Show(owner, "Il report non è stato creato:\n" + ex.Message +
                            "\n\nI dettagli sono nel registro attività.", MessageBoxImage.Warning);
                return null;
            }
            finally
            {
                Volatile.Write(ref _busy, 0);
            }

            Show(owner, "Report diagnostico creato:\n" + path +
                        "\n\nPuoi allegare lo ZIP così com'è, oppure usare «Copia riepilogo per la chat» per incollare un testo breve. " +
                        "I dati personali sono già stati rimossi.", MessageBoxImage.Information);
            SelectInExplorer(path);
            return path;
        }

        /// <summary>Copia negli appunti il riassunto di testo (max ~4000 caratteri). Restituisce il numero di caratteri o null.</summary>
        public static async Task<int?> CopySummaryAsync(PerfSession? session, Window? owner)
        {
            if (Interlocked.Exchange(ref _busy, 1) != 0) return null;
            string text;
            try
            {
                text = await ReportService.BuildSummaryTextAsync(session);
            }
            catch (Exception ex)
            {
                Show(owner, "Il riepilogo non è stato creato:\n" + ex.Message, MessageBoxImage.Warning);
                return null;
            }
            finally
            {
                Volatile.Write(ref _busy, 0);
            }

            // Gli appunti possono essere bloccati per un attimo da un'altra app: qualche tentativo prima di arrendersi.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    Clipboard.SetText(text);
                    Log.Info($"Riepilogo del report copiato negli appunti ({text.Length} caratteri).");
                    return text.Length;
                }
                catch (ExternalException ex)
                {
                    if (attempt >= 3)
                    {
                        Log.Warn("Copia del riepilogo negli appunti: " + ex.Message);
                        Show(owner, "Gli appunti di Windows sono occupati da un'altra app: riprova tra un attimo.", MessageBoxImage.Warning);
                        return null;
                    }
                    await Task.Delay(120);
                }
                catch (Exception ex)
                {
                    Log.Error("Copia del riepilogo negli appunti", ex);
                    Show(owner, "Copia negli appunti non riuscita: " + ex.Message, MessageBoxImage.Warning);
                    return null;
                }
            }
        }

        /// <summary>Apre Documenti\FN Boost\Report (creandola se manca).</summary>
        public static void OpenFolder()
        {
            try
            {
                var dir = ReportService.DefaultFolder;
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Error("Apertura cartella report", ex);
            }
        }

        private static void SelectInExplorer(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Warn("Apertura di Esplora file sul report: " + ex.Message);
            }
        }

        private static void Show(Window? owner, string text, MessageBoxImage icon)
        {
            if (owner != null) MessageBox.Show(owner, text, "FN Boost – report diagnostico", MessageBoxButton.OK, icon);
            else MessageBox.Show(text, "FN Boost – report diagnostico", MessageBoxButton.OK, icon);
        }
    }
}
