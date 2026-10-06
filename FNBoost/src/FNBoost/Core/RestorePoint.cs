using System;
using System.Management;
using Microsoft.Win32;

namespace FNBoost.Core
{
    /// <summary>Punto di ripristino di Windows (Protezione sistema) tramite l'API WMI ufficiale.</summary>
    public static class RestorePoint
    {
        private const string SrKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
        private const string FreqValue = "SystemRestorePointCreationFrequency";

        public static (bool ok, string message) Create(string description)
        {
            // Windows crea al massimo un punto ogni 24 ore: si sospende temporaneamente il limite
            // (valore documentato da Microsoft) e poi lo si rimette come era.
            var snapshot = RegistryUtil.Snapshot("HKLM", SrKey, FreqValue);
            try
            {
                RegistryUtil.Write("HKLM", SrKey, FreqValue, RegistryValueKind.DWord, 0);
            }
            catch (Exception ex)
            {
                Log.Warn("Impossibile modificare la frequenza dei punti di ripristino: " + ex.Message);
            }

            try
            {
                using var cls = new ManagementClass(@"\\.\root\default", "SystemRestore", null);
                using var inParams = cls.GetMethodParameters("CreateRestorePoint");
                inParams["Description"] = description;
                inParams["RestorePointType"] = 12;  // MODIFY_SETTINGS
                inParams["EventType"] = 100;        // BEGIN_SYSTEM_CHANGE
                using var result = cls.InvokeMethod("CreateRestorePoint", inParams, null);
                var code = Convert.ToUInt32(result?["ReturnValue"] ?? 1u);
                if (code == 0)
                {
                    Log.Info("Punto di ripristino creato: " + description);
                    return (true, "Punto di ripristino creato.");
                }
                var msg = code == 1058
                    ? "Protezione sistema disattivata sul disco C:. Attivala da: Pannello di controllo › Sistema › Protezione sistema › Configura."
                    : $"Windows ha rifiutato la creazione (codice {code}). Verifica che la Protezione sistema sia attiva su C:.";
                Log.Warn(msg);
                return (false, msg);
            }
            catch (Exception ex)
            {
                Log.Error("Creazione punto di ripristino", ex);
                return (false, "Impossibile creare il punto di ripristino: " + ex.Message);
            }
            finally
            {
                try { RegistryUtil.Restore(snapshot); } catch { /* ignorato */ }
            }
        }
    }
}
