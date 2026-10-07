using System.Collections.Generic;
using Microsoft.Win32;

namespace FNBoost.Core
{
    /// <summary>
    /// Elenco dei tweak. Sono inclusi solo interventi documentati, reversibili e che non
    /// riducono la sicurezza del sistema né toccano i file/processi di Fortnite
    /// (unica eccezione voluta: GameUserSettings.ini, gestito nella pagina Fortnite con backup).
    /// </summary>
    public static class TweakCatalog
    {
        private const string Mmcss = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
        private const string MmcssGames = Mmcss + @"\Tasks\Games";
        private const string Ifeo = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

        public static List<Tweak> Create() => new()
        {
            new RegistryTweak
            {
                Id = "game-mode",
                Category = "Windows – Gioco",
                Title = "Modalità Gioco attiva",
                Description = "Windows dà priorità al gioco in primo piano e sospende installazioni di driver e notifiche di riavvio di Windows Update durante la partita.",
                Details = "Predefinita su Windows 11 ma spesso disattivata da altri 'ottimizzatori'. Su CPU ibride (P-core/E-core come il tuo i5-13600K) aiuta lo scheduler a tenere il gioco sui P-core.",
                Technical = @"HKCU\Software\Microsoft\GameBar  AutoGameModeEnabled=1, AllowAutoGameMode=1",
                Impact = Impact.Medium,
                Risk = Risk.None,
                Recommended = true,
                Specs = new[]
                {
                    new RegSpec("HKCU", @"Software\Microsoft\GameBar", "AutoGameModeEnabled", RegistryValueKind.DWord, 1, null, MissingMeansDesired: true),
                    new RegSpec("HKCU", @"Software\Microsoft\GameBar", "AllowAutoGameMode", RegistryValueKind.DWord, 1, null, MissingMeansDesired: true),
                }
            },

            new RegistryTweak
            {
                Id = "game-dvr-off",
                Category = "Windows – Gioco",
                Title = "Disattiva registrazione in background di Xbox Game Bar",
                Description = "Ferma la cattura video continua ('Registra ciò che è successo') che occupa GPU/encoder e disco mentre giochi.",
                Details = "È uno dei consigli più citati nelle guide per stutter e cali di FPS. Disattiva anche le catture di Game Bar (Win+Alt+R): per registrare puoi usare NVIDIA App, AMD Adrenalin o OBS.",
                Technical = @"HKCU\System\GameConfigStore GameDVR_Enabled=0 · HKCU\...\GameDVR AppCaptureEnabled=0, HistoricalCaptureEnabled=0",
                Impact = Impact.Medium,
                Risk = Risk.None,
                Recommended = true,
                Specs = new[]
                {
                    new RegSpec("HKCU", @"System\GameConfigStore", "GameDVR_Enabled", RegistryValueKind.DWord, 0, 1),
                    new RegSpec("HKCU", @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", RegistryValueKind.DWord, 0, null),
                    new RegSpec("HKCU", @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled", RegistryValueKind.DWord, 0, null, MissingMeansDesired: true),
                }
            },

            new PowerPlanTweak
            {
                Id = "power-high",
                Category = "Windows – Sistema",
                Title = "Piano energetico 'Prestazioni elevate'",
                Description = "La CPU non scende nei P-state più bassi e non 'parcheggia' i core: meno variazioni di frequenza = frametime più costante.",
                Details = "Su desktop (come il tuo Z790) è sicuro: aumenta solo un po' i consumi a riposo. Il ripristino riattiva il piano che avevi prima.",
                Technical = "powercfg /setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
                Impact = Impact.Medium,
                Risk = Risk.Low,
                Recommended = true,
            },

            new RegistryTweak
            {
                Id = "hags-on",
                Category = "Windows – Grafica",
                Title = "Pianificazione GPU con accelerazione hardware (HAGS)",
                Description = "La GPU gestisce da sola la propria coda di lavoro. Necessaria per DLSS Frame Generation sulle schede NVIDIA RTX 40/50.",
                Details = "I test mostrano risultati variabili (spesso neutri, a volte frametime migliori). Se dopo il riavvio noti più stutter, disattivala: il ripristino è immediato. Se la tua GPU non la supporta, Windows la ignora.",
                Technical = @"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers  HwSchMode=2",
                Impact = Impact.Variable,
                Risk = Risk.Low,
                RequiresReboot = true,
                Recommended = true,
                Specs = new[]
                {
                    new RegSpec("HKLM", @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", RegistryValueKind.DWord, 2, null),
                }
            },

            new DxUserSettingTweak
            {
                Id = "windowed-opt",
                Category = "Windows – Grafica",
                Title = "Ottimizzazioni per i giochi in finestra",
                Description = "Usa il modello di presentazione 'flip' anche per i giochi DirectX 10/11 in finestra o senza bordi: meno latenza, VRR e Auto HDR funzionanti.",
                Details = "Può contare per Fortnite in modalità Performance, che gira su Direct3D 11 (feature level ES3_1, lo scrive il gioco nel suo log), " +
                          "quando giochi in 'Schermo intero in finestra': se il gioco presenta i frame con il vecchio modello 'blt', Windows li copia e li compone " +
                          "col desktop (più latenza, niente VRR); con questa opzione li passa al flip model, con latenza vicina allo schermo intero. " +
                          "In DirectX 12, in schermo intero esclusivo o se il gioco usa già il flip model non cambia nulla. Opzione ufficiale di Impostazioni › Schermo › Grafica.",
                Technical = @"HKCU\Software\Microsoft\DirectX\UserGpuPreferences  DirectXUserGlobalSettings: SwapEffectUpgradeEnable=1",
                // Variabile: nessun effetto in DirectX 12 o a schermo intero, utile in Performance (D3D11) in finestra.
                Impact = Impact.Variable,
                Risk = Risk.None,
                Recommended = true,
                ValueNameProvider = () => "DirectXUserGlobalSettings",
                SettingKey = "SwapEffectUpgradeEnable",
                SettingValue = "1",
            },

            new DxUserSettingTweak
            {
                Id = "fn-gpu-high",
                Category = "Fortnite",
                Title = "Fortnite sulla GPU ad alte prestazioni",
                Description = "Forza Windows a usare la scheda video dedicata per Fortnite (come Impostazioni › Grafica › Prestazioni elevate).",
                Details = "Il tuo i5-13600K ha una GPU integrata (UHD 770): se è attiva nel BIOS, Windows potrebbe assegnare il gioco alla GPU sbagliata. Se hai solo la dedicata, non cambia nulla.",
                Technical = @"HKCU\Software\Microsoft\DirectX\UserGpuPreferences  <percorso FortniteClient-Win64-Shipping.exe> = GpuPreference=2;",
                Impact = Impact.High,
                Risk = Risk.None,
                Recommended = true,
                ValueNameProvider = () => FortniteLocator.ClientExe,
                MissingReason = "Fortnite non trovato (installa il gioco dall'Epic Games Launcher).",
                SettingKey = "GpuPreference",
                SettingValue = "2",
            },

            new RegistryTweak
            {
                Id = "fn-cpu-priority",
                Category = "Fortnite",
                Title = "Priorità CPU 'Alta' per Fortnite all'avvio",
                Description = "Windows avvia Fortnite con priorità Alta, così processi in background (browser, Discord, launcher) interferiscono meno.",
                Details = "Usa una funzione standard di Windows (Image File Execution Options › PerfOptions) applicata al lancio: l'app non apre né modifica il processo del gioco. Effetto piccolo ma utile se hai molte app aperte.",
                Technical = @"HKLM\...\Image File Execution Options\FortniteClient-Win64-Shipping.exe\PerfOptions  CpuPriorityClass=3",
                Impact = Impact.Low,
                Risk = Risk.Low,
                Recommended = false,
                Specs = new[]
                {
                    new RegSpec("HKLM", Ifeo + @"\" + FortniteLocator.ClientExeName + @"\PerfOptions", "CpuPriorityClass",
                        RegistryValueKind.DWord, 3, null, PruneUpTo: Ifeo),
                }
            },

            new RegistryTweak
            {
                Id = "mmcss-games",
                Category = "Windows – Sistema",
                Title = "Priorità multimediale per i giochi (MMCSS)",
                Description = "Riduce la quota di CPU riservata alle attività in background e alza la priorità della classe 'Games' del Multimedia Class Scheduler.",
                Details = "Tweak molto diffuso ma con effetto misurabile basso: è innocuo e reversibile. SystemResponsiveness=10 è il minimo reale (0 viene trattato come 10 da Windows).",
                Technical = @"SystemProfile: SystemResponsiveness=10, NetworkThrottlingIndex=0xFFFFFFFF · Tasks\Games: Priority=6, Scheduling Category=High, SFIO Priority=High, GPU Priority=8",
                Impact = Impact.Low,
                Risk = Risk.None,
                Recommended = false,
                Specs = new[]
                {
                    new RegSpec("HKLM", Mmcss, "SystemResponsiveness", RegistryValueKind.DWord, 10, 20),
                    new RegSpec("HKLM", Mmcss, "NetworkThrottlingIndex", RegistryValueKind.DWord, unchecked((int)0xFFFFFFFF), 10),
                    new RegSpec("HKLM", MmcssGames, "Priority", RegistryValueKind.DWord, 6, 2),
                    new RegSpec("HKLM", MmcssGames, "Scheduling Category", RegistryValueKind.String, "High", "Medium"),
                    new RegSpec("HKLM", MmcssGames, "SFIO Priority", RegistryValueKind.String, "High", "Normal"),
                    new RegSpec("HKLM", MmcssGames, "GPU Priority", RegistryValueKind.DWord, 8, 8),
                }
            },

            new RegistryTweak
            {
                Id = "mouse-accel-off",
                Category = "Windows – Input",
                Title = "Disattiva 'Precisione puntatore avanzata'",
                Description = "Movimento del mouse 1:1 sul desktop e nei menu.",
                Details = "In partita Fortnite usa già l'input raw (ignora l'accelerazione di Windows), quindi la mira non cambia: serve solo per coerenza tra desktop, menu e lobby.",
                Technical = @"HKCU\Control Panel\Mouse  MouseSpeed=0, MouseThreshold1=0, MouseThreshold2=0",
                Impact = Impact.MenuOnly,
                Risk = Risk.None,
                Recommended = false,
                Specs = new[]
                {
                    new RegSpec("HKCU", @"Control Panel\Mouse", "MouseSpeed", RegistryValueKind.String, "0", "1"),
                    new RegSpec("HKCU", @"Control Panel\Mouse", "MouseThreshold1", RegistryValueKind.String, "0", "6"),
                    new RegSpec("HKCU", @"Control Panel\Mouse", "MouseThreshold2", RegistryValueKind.String, "0", "10"),
                },
                AfterChange = ApplyMouseSettingsLive,
            },

            new PageFileTweak
            {
                Id = "pagefile-auto",
                Category = "Windows – Sistema",
                Title = "File di paging gestito da Windows",
                Description = "Ripristina la gestione automatica del file di paging. Un file piccolo e fisso (es. 2 GB) può causare crash 'memoria insufficiente' e scatti quando la memoria impegnata cresce (Fortnite + browser + Discord).",
                Details = "Nel tuo sistema risulta 'Page File Space 2.00 GB' con 9,8 GB di memoria virtuale libera su 33,8: se il file è fisso, è il primo sospettato per crash e micro-freeze. È l'impostazione predefinita di Windows.",
                Technical = @"HKLM\...\Memory Management  PagingFiles = ?:\pagefile.sys",
                Impact = Impact.Stability,
                Risk = Risk.None,
                RequiresReboot = true,
                Recommended = true,
                Specs = new[]
                {
                    new RegSpec("HKLM", PageFileTweak.MmPath, "PagingFiles", RegistryValueKind.MultiString,
                        new[] { @"?:\pagefile.sys" }, new[] { @"?:\pagefile.sys" }),
                }
            },
        };

        private static void ApplyMouseSettingsLive()
        {
            try
            {
                int Read(string name) =>
                    int.TryParse(RegistryUtil.ReadString("HKCU", @"Control Panel\Mouse", name), out var v) ? v : 0;
                var values = new[] { Read("MouseThreshold1"), Read("MouseThreshold2"), Read("MouseSpeed") };
                Native.SystemParametersInfo(Native.SPI_SETMOUSE, 0, values, Native.SPIF_SENDCHANGE);
            }
            catch (System.Exception ex)
            {
                Log.Warn("Aggiornamento live del mouse non riuscito: " + ex.Message);
            }
        }
    }
}
