using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;

namespace FNBoost.Core
{
    public sealed class DiagnosticCheck
    {
        public string Title { get; init; } = "";
        public CheckStatus Status { get; init; }
        public string Message { get; init; } = "";
        public string Hint { get; init; } = "";

        public string Icon => Status switch
        {
            CheckStatus.Ok => "✔",
            CheckStatus.Info => "ℹ",
            CheckStatus.Warn => "⚠",
            _ => "✖"
        };
    }

    public sealed class GpuInfo
    {
        public string Name { get; init; } = "";
        public string Vendor { get; init; } = "";
        public string DriverVersion { get; init; } = "";
        public DateTime? DriverDate { get; init; }
    }

    public sealed class DisplayInfo
    {
        public string Device { get; init; } = "";
        public int Width { get; init; }
        public int Height { get; init; }
        public int CurrentHz { get; init; }
        public int MaxHz { get; init; }
        public bool Primary { get; init; }
        public override string ToString() =>
            $"{Width}×{Height} @ {CurrentHz} Hz{(MaxHz > CurrentHz ? $" (max {MaxHz} Hz)" : "")}{(Primary ? " · principale" : "")}";
    }

    public sealed class SystemSnapshot
    {
        public string Os { get; set; } = "";
        public string Cpu { get; set; } = "";
        public int Cores { get; set; }
        public int Threads { get; set; }
        public double RamTotalGb { get; set; }
        public double RamAvailGb { get; set; }
        public string RamType { get; set; } = "";
        public int RamConfiguredMts { get; set; }
        public int RamRatedMts { get; set; }
        public int RamModules { get; set; }
        public List<GpuInfo> Gpus { get; } = new();
        public List<DisplayInfo> Displays { get; } = new();
        public string Board { get; set; } = "";
        public string BiosVersion { get; set; } = "";
        public DateTime? BiosDate { get; set; }
        public bool? SecureBoot { get; set; }
        public bool? Tpm { get; set; }
        public int? VbsStatus { get; set; }
        public bool? HvciRunning { get; set; }
        public string[] PagingFiles { get; set; } = Array.Empty<string>();
        public int? PageFileAllocatedMb { get; set; }
        public double CommitUsedGb { get; set; }
        public double CommitLimitGb { get; set; }
        public string PowerPlan { get; set; } = "";
        public string? PowerPlanGuid { get; set; }
        public string? FortniteDir { get; set; }
        public double? FortniteDriveFreeGb { get; set; }
        public string? FortniteDiskType { get; set; }
        public List<DiagnosticCheck> Checks { get; } = new();

        public string GpuText => Gpus.Count == 0 ? "Non rilevata" : string.Join("\n", Gpus.Select(g => g.Name));
        public string GpuDriverText => Gpus.Count == 0 ? "" :
            string.Join("\n", Gpus.Select(g => $"Driver {g.DriverVersion}{(g.DriverDate is { } d ? $" ({d:dd/MM/yyyy})" : "")}"));
        public string DisplayText => Displays.Count == 0 ? "Non rilevato" : string.Join("\n", Displays.Select(d => d.ToString()));
        public string RamText => $"{RamTotalGb:0.#} GB {RamType}".Trim();
        public string RamDetailText => RamConfiguredMts > 0
            ? $"{RamConfiguredMts} MT/s · {RamModules} moduli · libera {RamAvailGb:0.#} GB"
            : $"libera {RamAvailGb:0.#} GB";
        public string CpuDetailText => $"{Cores} core · {Threads} thread";
        public string BoardText => $"{Board}\nBIOS {BiosVersion}{(BiosDate is { } d ? $" ({d:dd/MM/yyyy})" : "")}";
    }

    public static class SystemDiagnostics
    {
        public static SystemSnapshot Collect(IEnumerable<Tweak> tweaks)
        {
            var s = new SystemSnapshot();
            Try(() => ReadOs(s));
            Try(() => ReadCpu(s));
            Try(() => ReadRam(s));
            Try(() => ReadGpus(s));
            Try(() => ReadDisplays(s));
            Try(() => ReadBios(s));
            Try(() => ReadSecurity(s));
            Try(() => ReadPageFile(s));
            Try(() => ReadPower(s));
            Try(() => ReadFortnite(s));
            Try(() => BuildChecks(s, tweaks.ToList()));
            return s;
        }

        private static void Try(Action a)
        {
            try { a(); }
            catch (Exception ex) { Log.Warn("Diagnostica: " + ex.Message); }
        }

        private static IEnumerable<ManagementObject> Wmi(string scope, string query)
        {
            using var searcher = new ManagementObjectSearcher(scope, query);
            searcher.Options.Timeout = TimeSpan.FromSeconds(10);
            foreach (ManagementObject o in searcher.Get()) yield return o;
        }

        private static void ReadOs(SystemSnapshot s)
        {
            const string k = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
            var build = RegistryUtil.ReadString("HKLM", k, "CurrentBuild") ?? "";
            var ubr = RegistryUtil.ReadDword("HKLM", k, "UBR");
            var display = RegistryUtil.ReadString("HKLM", k, "DisplayVersion") ?? "";
            var edition = RegistryUtil.ReadString("HKLM", k, "EditionID") ?? "";
            var name = int.TryParse(build, out var b) && b >= 22000 ? "Windows 11" : "Windows 10";
            s.Os = $"{name} {edition} {display} (build {build}{(ubr != null ? "." + ubr : "")})";
        }

        private static void ReadCpu(SystemSnapshot s)
        {
            s.Cpu = (RegistryUtil.ReadString("HKLM", @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString") ?? "").Trim();
            s.Threads = Environment.ProcessorCount;
            foreach (var o in Wmi(@"root\cimv2", "SELECT NumberOfCores FROM Win32_Processor"))
                s.Cores += Convert.ToInt32(o["NumberOfCores"] ?? 0);
        }

        private static void ReadRam(SystemSnapshot s)
        {
            var m = Native.GetMemoryStatus();
            s.RamTotalGb = m.ullTotalPhys / 1073741824.0;
            s.RamAvailGb = m.ullAvailPhys / 1073741824.0;
            s.CommitLimitGb = m.ullTotalPageFile / 1073741824.0;
            s.CommitUsedGb = (m.ullTotalPageFile - m.ullAvailPageFile) / 1073741824.0;

            foreach (var o in Wmi(@"root\cimv2", "SELECT Speed, ConfiguredClockSpeed, SMBIOSMemoryType FROM Win32_PhysicalMemory"))
            {
                s.RamModules++;
                var conf = Convert.ToInt32(o["ConfiguredClockSpeed"] ?? 0);
                var rated = Convert.ToInt32(o["Speed"] ?? 0);
                var type = Convert.ToInt32(o["SMBIOSMemoryType"] ?? 0);
                if (conf > 0) s.RamConfiguredMts = s.RamConfiguredMts == 0 ? conf : Math.Min(s.RamConfiguredMts, conf);
                s.RamRatedMts = Math.Max(s.RamRatedMts, rated);
                s.RamType = type switch { 26 => "DDR4", 34 => "DDR5", 24 => "DDR3", 35 => "LPDDR5", 30 => "LPDDR4", _ => s.RamType };
            }
        }

        private static void ReadGpus(SystemSnapshot s)
        {
            foreach (var o in Wmi(@"root\cimv2", "SELECT Name, AdapterCompatibility, DriverVersion, DriverDate FROM Win32_VideoController"))
            {
                var name = Convert.ToString(o["Name"]) ?? "";
                if (name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Remote", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)) continue;
                DateTime? date = null;
                try
                {
                    var raw = Convert.ToString(o["DriverDate"]);
                    if (!string.IsNullOrEmpty(raw)) date = ManagementDateTimeConverter.ToDateTime(raw);
                }
                catch { /* data non valida */ }
                s.Gpus.Add(new GpuInfo
                {
                    Name = name,
                    Vendor = Convert.ToString(o["AdapterCompatibility"]) ?? "",
                    DriverVersion = Convert.ToString(o["DriverVersion"]) ?? "",
                    DriverDate = date
                });
            }
            // Dedicata prima dell'integrata
            s.Gpus.Sort((a, b) => IsIntegrated(a).CompareTo(IsIntegrated(b)));
        }

        public static bool IsIntegrated(GpuInfo g) =>
            g.Name.Contains("UHD", StringComparison.OrdinalIgnoreCase) ||
            g.Name.Contains("Iris", StringComparison.OrdinalIgnoreCase) ||
            (g.Name.Contains("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase));

        public static List<DisplayInfo> ReadDisplayList()
        {
            var list = new List<DisplayInfo>();
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref Native.RECT _, IntPtr _) =>
            {
                var mi = new Native.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFOEX>() };
                if (!Native.GetMonitorInfo(hMon, ref mi)) return true;
                var cur = new Native.DEVMODE { dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf<Native.DEVMODE>() };
                if (!Native.EnumDisplaySettings(mi.szDevice, Native.ENUM_CURRENT_SETTINGS, ref cur)) return true;
                int max = cur.dmDisplayFrequency;
                var mode = new Native.DEVMODE { dmSize = cur.dmSize };
                for (int i = 0; Native.EnumDisplaySettings(mi.szDevice, i, ref mode) && i < 2000; i++)
                {
                    if (mode.dmPelsWidth == cur.dmPelsWidth && mode.dmPelsHeight == cur.dmPelsHeight)
                        max = Math.Max(max, mode.dmDisplayFrequency);
                }
                list.Add(new DisplayInfo
                {
                    Device = mi.szDevice,
                    Width = cur.dmPelsWidth,
                    Height = cur.dmPelsHeight,
                    CurrentHz = cur.dmDisplayFrequency,
                    MaxHz = max,
                    Primary = (mi.dwFlags & 1) != 0
                });
                return true;
            }, IntPtr.Zero);
            return list.OrderByDescending(d => d.Primary).ToList();
        }

        private static void ReadDisplays(SystemSnapshot s) => s.Displays.AddRange(ReadDisplayList());

        private static void ReadBios(SystemSnapshot s)
        {
            const string k = @"HARDWARE\DESCRIPTION\System\BIOS";
            s.Board = $"{RegistryUtil.ReadString("HKLM", k, "BaseBoardManufacturer")} {RegistryUtil.ReadString("HKLM", k, "BaseBoardProduct")}".Trim();
            s.BiosVersion = RegistryUtil.ReadString("HKLM", k, "BIOSVersion") ?? "";
            var date = RegistryUtil.ReadString("HKLM", k, "BIOSReleaseDate");
            if (DateTime.TryParseExact(date, "MM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                s.BiosDate = d;
        }

        private static void ReadSecurity(SystemSnapshot s)
        {
            var sb = RegistryUtil.ReadDword("HKLM", @"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");
            s.SecureBoot = sb == null ? null : sb == 1;
            try
            {
                foreach (var o in Wmi(@"root\cimv2\Security\MicrosoftTpm", "SELECT IsEnabled_InitialValue FROM Win32_Tpm"))
                    s.Tpm = Convert.ToBoolean(o["IsEnabled_InitialValue"] ?? false);
                s.Tpm ??= false;
            }
            catch { /* WMI TPM non disponibile */ }
            try
            {
                foreach (var o in Wmi(@"root\Microsoft\Windows\DeviceGuard", "SELECT VirtualizationBasedSecurityStatus, SecurityServicesRunning FROM Win32_DeviceGuard"))
                {
                    s.VbsStatus = Convert.ToInt32(o["VirtualizationBasedSecurityStatus"] ?? 0);
                    var running = o["SecurityServicesRunning"] as uint[] ?? Array.Empty<uint>();
                    s.HvciRunning = running.Contains(2u);
                }
            }
            catch { /* DeviceGuard non disponibile */ }
        }

        private static void ReadPageFile(SystemSnapshot s)
        {
            s.PagingFiles = PageFileTweak.ReadPagingFiles();
            foreach (var o in Wmi(@"root\cimv2", "SELECT AllocatedBaseSize FROM Win32_PageFileUsage"))
                s.PageFileAllocatedMb = (s.PageFileAllocatedMb ?? 0) + Convert.ToInt32(o["AllocatedBaseSize"] ?? 0);
        }

        private static void ReadPower(SystemSnapshot s)
        {
            var (guid, name) = PowerPlanTweak.GetActive();
            s.PowerPlanGuid = guid;
            s.PowerPlan = name;
        }

        private static void ReadFortnite(SystemSnapshot s)
        {
            s.FortniteDir = FortniteLocator.InstallDir;
            if (s.FortniteDir == null) return;
            var root = Path.GetPathRoot(s.FortniteDir);
            if (root == null) return;
            var drive = new DriveInfo(root);
            s.FortniteDriveFreeGb = drive.AvailableFreeSpace / 1073741824.0;
            try
            {
                var letter = root.TrimEnd('\\', ':');
                uint? diskNumber = null;
                foreach (var p in Wmi(@"root\Microsoft\Windows\Storage", $"SELECT DiskNumber FROM MSFT_Partition WHERE DriveLetter = '{letter}'"))
                    diskNumber = Convert.ToUInt32(p["DiskNumber"]);
                if (diskNumber != null)
                {
                    foreach (var d in Wmi(@"root\Microsoft\Windows\Storage", $"SELECT MediaType, BusType FROM MSFT_PhysicalDisk WHERE DeviceId = '{diskNumber}'"))
                    {
                        var media = Convert.ToInt32(d["MediaType"] ?? 0);
                        var bus = Convert.ToInt32(d["BusType"] ?? 0);
                        s.FortniteDiskType = bus == 17 ? "SSD NVMe" : media switch { 4 => "SSD", 3 => "HDD", _ => null };
                    }
                }
            }
            catch { /* tipo disco non disponibile */ }
        }

        private static TweakState StateOf(List<Tweak> tweaks, string id) =>
            tweaks.FirstOrDefault(t => t.Id == id)?.State ?? TweakState.Unknown;

        private static void BuildChecks(SystemSnapshot s, List<Tweak> tweaks)
        {
            var c = s.Checks;

            // 1. Frequenza monitor
            foreach (var d in s.Displays)
            {
                if (d.MaxHz > d.CurrentHz + 1)
                    c.Add(new DiagnosticCheck
                    {
                        Title = "Frequenza del monitor",
                        Status = CheckStatus.Bad,
                        Message = $"{d.Device}: impostato a {d.CurrentHz} Hz ma supporta {d.MaxHz} Hz.",
                        Hint = "Impostazioni › Sistema › Schermo › Schermo avanzato › Scegli una frequenza di aggiornamento. È l'errore più comune: stai vedendo meno frame di quelli che il PC genera."
                    });
                else
                    c.Add(new DiagnosticCheck
                    {
                        Title = "Frequenza del monitor",
                        Status = CheckStatus.Ok,
                        Message = $"{d.Device}: {d.Width}×{d.Height} alla frequenza massima ({d.CurrentHz} Hz)."
                    });
            }

            // 2. RAM / XMP
            if (s.RamConfiguredMts > 0)
            {
                bool slow = (s.RamType == "DDR5" && s.RamConfiguredMts <= 4800) ||
                            (s.RamType == "DDR4" && s.RamConfiguredMts <= 2666);
                c.Add(slow
                    ? new DiagnosticCheck
                    {
                        Title = "Velocità RAM (XMP)",
                        Status = CheckStatus.Warn,
                        Message = $"La RAM {s.RamType} lavora a {s.RamConfiguredMts} MT/s (velocità base JEDEC).",
                        Hint = "Se il tuo kit è più veloce (es. DDR5-6000), attiva il profilo XMP nel BIOS: Gigabyte › Tweaker › Extreme Memory Profile (X.M.P.). Quando Fortnite è limitato dalla CPU (FPS alti, impostazioni basse) è uno dei guadagni più grandi su 1% low e stutter."
                    }
                    : new DiagnosticCheck
                    {
                        Title = "Velocità RAM (XMP)",
                        Status = CheckStatus.Ok,
                        Message = $"{s.RamType} a {s.RamConfiguredMts} MT/s: profilo XMP/EXPO probabilmente attivo."
                    });
            }

            // 3. File di paging / memoria impegnata
            var fixedMax = PageFileTweak.FixedMaxMb(s.PagingFiles);
            if (s.PagingFiles.Length == 0)
                c.Add(new DiagnosticCheck
                {
                    Title = "File di paging",
                    Status = CheckStatus.Bad,
                    Message = "Il file di paging è disattivato.",
                    Hint = "Fortnite e i driver video possono andare in crash quando la memoria impegnata supera la RAM. Usa il tweak 'File di paging gestito da Windows'."
                });
            else if (!PageFileTweak.IsSystemManaged(s.PagingFiles) && fixedMax is < 16384)
                c.Add(new DiagnosticCheck
                {
                    Title = "File di paging",
                    Status = CheckStatus.Warn,
                    Message = $"File di paging fisso di {fixedMax / 1024.0:0.#} GB.",
                    Hint = "Un file piccolo e fisso limita la memoria impegnata (commit). Usa il tweak 'File di paging gestito da Windows' (riavvio richiesto)."
                });
            else
                c.Add(new DiagnosticCheck
                {
                    Title = "File di paging",
                    Status = CheckStatus.Ok,
                    Message = PageFileTweak.IsSystemManaged(s.PagingFiles)
                        ? $"Gestito da Windows{(s.PageFileAllocatedMb is { } mb ? $" (attualmente {mb / 1024.0:0.#} GB, cresce se serve)" : "")}."
                        : $"Dimensione fissa adeguata ({fixedMax / 1024.0:0.#} GB)."
                });

            if (s.CommitLimitGb > 0 && s.CommitUsedGb / s.CommitLimitGb > 0.8)
                c.Add(new DiagnosticCheck
                {
                    Title = "Memoria impegnata",
                    Status = CheckStatus.Warn,
                    Message = $"In uso {s.CommitUsedGb:0.#} GB su un limite di {s.CommitLimitGb:0.#} GB.",
                    Hint = "Chiudi le app pesanti (browser con molte schede) prima di giocare, o lascia che Windows gestisca il file di paging."
                });

            // 4. Piano energetico
            bool highPerf = s.PowerPlanGuid == PowerPlanTweak.HighPerformance ||
                            StateOf(tweaks, "power-high") == TweakState.Applied ||
                            s.PowerPlan.Contains("Ultimate", StringComparison.OrdinalIgnoreCase) ||
                            s.PowerPlan.Contains("ottimal", StringComparison.OrdinalIgnoreCase) ||
                            s.PowerPlan.Contains("elevat", StringComparison.OrdinalIgnoreCase) ||
                            s.PowerPlan.Contains("High", StringComparison.OrdinalIgnoreCase);
            c.Add(new DiagnosticCheck
            {
                Title = "Piano energetico",
                Status = highPerf ? CheckStatus.Ok : CheckStatus.Info,
                Message = $"Piano attivo: {s.PowerPlan}.",
                Hint = highPerf ? "" : "Il tweak 'Prestazioni elevate' rende più stabili frequenze e frametime."
            });

            // 5-6. Modalità gioco / Game DVR
            c.Add(StateCheck(tweaks, "game-mode", "Modalità Gioco", "attiva", "non attiva: applica il tweak."));
            c.Add(StateCheck(tweaks, "game-dvr-off", "Registrazione in background", "disattivata", "potrebbe essere attiva: applica il tweak."));

            // 7. Sicurezza richiesta da Fortnite
            if (s.SecureBoot == true && s.Tpm != false)
                c.Add(new DiagnosticCheck
                {
                    Title = "Secure Boot / TPM",
                    Status = CheckStatus.Ok,
                    Message = "Attivi. Epic li richiede (insieme a IOMMU) per i tornei di Fortnite: non disattivarli.",
                });
            else
                c.Add(new DiagnosticCheck
                {
                    Title = "Secure Boot / TPM",
                    Status = CheckStatus.Warn,
                    Message = $"Secure Boot: {(s.SecureBoot == true ? "sì" : "no")} · TPM: {(s.Tpm == true ? "sì" : s.Tpm == null ? "?" : "no")}.",
                    Hint = "Per i tornei Fortnite servono Secure Boot, TPM 2.0 e IOMMU attivi nel BIOS."
                });

            // 8. VBS / Integrità memoria
            if (s.HvciRunning == true)
                c.Add(new DiagnosticCheck
                {
                    Title = "Integrità della memoria (HVCI)",
                    Status = CheckStatus.Info,
                    Message = "Attiva. In alcuni giochi costa qualche punto percentuale di FPS.",
                    Hint = "FN Boost non la disattiva perché riduce la sicurezza. Se vuoi provarci: Sicurezza di Windows › Sicurezza dispositivi › Isolamento core."
                });
            else if (s.VbsStatus != null)
                c.Add(new DiagnosticCheck
                {
                    Title = "Integrità della memoria (HVCI)",
                    Status = CheckStatus.Ok,
                    Message = s.VbsStatus == 2
                        ? "Non attiva (VBS è in esecuzione ma senza servizi pesanti): impatto trascurabile."
                        : "Non attiva."
                });

            // 9. CPU Intel 13a/14a gen: microcode
            if (Regex.IsMatch(s.Cpu, @"1[34]th Gen|i[3579]-1[34]\d{3}", RegexOptions.IgnoreCase))
            {
                bool oldBios = s.BiosDate != null && s.BiosDate < new DateTime(2024, 10, 1);
                c.Add(new DiagnosticCheck
                {
                    Title = "BIOS / microcode Intel",
                    Status = oldBios ? CheckStatus.Bad : CheckStatus.Ok,
                    Message = oldBios
                        ? $"BIOS {s.BiosVersion} del {s.BiosDate:dd/MM/yyyy}: precedente alle correzioni microcode Intel (0x12B+)."
                        : $"BIOS {s.BiosVersion}{(s.BiosDate is { } d ? $" del {d:dd/MM/yyyy}" : "")}: include le correzioni microcode per l'instabilità dei 13ª/14ª gen.",
                    Hint = oldBios ? "Aggiorna il BIOS dal sito Gigabyte e usa il profilo 'Intel Default Settings'. Crash e stutter casuali sui 13600K/14600K spesso derivano da qui." : ""
                });
            }

            // 10. Driver GPU
            var dGpu = s.Gpus.FirstOrDefault(g => !IsIntegrated(g));
            if (dGpu?.DriverDate is { } dd)
            {
                var age = DateTime.Now - dd;
                c.Add(new DiagnosticCheck
                {
                    Title = "Driver scheda video",
                    Status = age.TotalDays > 180 ? CheckStatus.Warn : CheckStatus.Ok,
                    Message = $"{dGpu.Name}: driver {dGpu.DriverVersion} del {dd:dd/MM/yyyy}.",
                    Hint = age.TotalDays > 180 ? "Aggiorna i driver (NVIDIA App / AMD Adrenalin / Intel Arc). I driver 'Game Ready' contengono spesso fix per Fortnite." : ""
                });
            }
            if (s.Gpus.Any(IsIntegrated) && dGpu != null)
                c.Add(new DiagnosticCheck
                {
                    Title = "GPU integrata attiva",
                    Status = CheckStatus.Info,
                    Message = "Sono attive sia la GPU integrata sia la dedicata.",
                    Hint = "Collega il monitor alla scheda video dedicata e applica il tweak 'Fortnite sulla GPU ad alte prestazioni'."
                });

            // 11. Fortnite
            if (s.FortniteDir == null)
                c.Add(new DiagnosticCheck
                {
                    Title = "Fortnite",
                    Status = CheckStatus.Info,
                    Message = "Installazione non trovata.",
                    Hint = "I tweak specifici per Fortnite si attivano quando il gioco è installato tramite Epic Games Launcher."
                });
            else
            {
                c.Add(new DiagnosticCheck
                {
                    Title = "Fortnite",
                    Status = CheckStatus.Ok,
                    Message = $"Installato in {s.FortniteDir}{(s.FortniteDiskType != null ? $" ({s.FortniteDiskType})" : "")}."
                });
                if (s.FortniteDiskType == "HDD")
                    c.Add(new DiagnosticCheck
                    {
                        Title = "Disco di Fortnite",
                        Status = CheckStatus.Bad,
                        Message = "Fortnite è su un disco meccanico (HDD).",
                        Hint = "Lo streaming di texture e shader da HDD causa forti stutter: spostalo su SSD (Epic Launcher › Libreria › Gestisci › Sposta)."
                    });
                if (s.FortniteDriveFreeGb is < 25)
                    c.Add(new DiagnosticCheck
                    {
                        Title = "Spazio libero",
                        Status = CheckStatus.Warn,
                        Message = $"Solo {s.FortniteDriveFreeGb:0.#} GB liberi sul disco di Fortnite.",
                        Hint = "Gli aggiornamenti e la cache shader richiedono spazio: libera almeno 30 GB."
                    });
            }
        }

        private static DiagnosticCheck StateCheck(List<Tweak> tweaks, string id, string title, string ok, string notOk)
        {
            var st = StateOf(tweaks, id);
            return new DiagnosticCheck
            {
                Title = title,
                Status = st == TweakState.Applied ? CheckStatus.Ok : CheckStatus.Info,
                Message = st == TweakState.Applied ? $"{title}: {ok}." : $"{title}: {notOk}"
            };
        }
    }
}
