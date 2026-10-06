using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using FNBoost.Core;
using FNBoost.Perf;

namespace FNBoost.Tests
{
    /// <summary>Test della misura di rete e dei processi: statistiche del ping, decodifica ETW, freeze e analisi.</summary>
    internal static class NetTests
    {
        private const string Fn = "FortniteClient-Win64-Shipping";

        public static void RunAll()
        {
            Console.WriteLine("NetStats");
            T.Run("jitter (con ping persi) e variante RFC 3550", JitterMath);
            T.Run("percentile, mediana e perdita", PercentileAndLoss);
            T.Run("Summarize con null, vuoto e tutto perso", SummarizeTests);
            T.Run("indirizzi pubblici e privati (privacy)", PublicAddresses);
            T.Run("payload Kernel-Network IPv4/IPv6 e lato remoto", PayloadParsing);
            T.Run("rilevamento freeze", FreezeDetection);
            T.Run("aggregatore: secondi, server, freeze, fuori ordine, cambio server", Aggregator);
            T.Run("correlazione freeze/stutter", Correlation);
            T.Run("segnale Wi-Fi da netsh (lingue diverse)", WifiParsing);
            T.Run("nomi processo, esclusioni e accumulo CPU/RAM", ProcessUsageTests);
            T.Run("nomi delle regioni e testi/soglie per la UI", DisplayHelpers);

            Console.WriteLine("PerfAnalyzer (rete e processi)");
            T.Run("ping alto: server lontano rispetto alla regione migliore", AnalyzerHighPing);
            T.Run("Wi-Fi debole e router instabile", AnalyzerHomeNetwork);
            T.Run("perdita di pacchetti", AnalyzerLoss);
            T.Run("banda usata dalle altre app", AnalyzerBackgroundBandwidth);
            T.Run("freeze di rete contro stutter degli FPS", AnalyzerFreezesVsStutters);
            T.Run("processi in background pesanti", AnalyzerProcesses);
            T.Run("confronto con la sessione precedente: delta di rete", AnalyzerNetComparison);
            T.Run("sessioni vecchie senza rete e rete senza ping", AnalyzerWithoutNetwork);
        }

        // ================= NetStats =================

        private static void JitterMath()
        {
            T.Near(7.0 / 3, NetStats.Jitter(new double?[] { 10, 12, 11, 15 }) ?? double.NaN, 1e-9, "media |Δ|");
            T.Near(2.5, NetStats.Jitter(new double?[] { 10, null, 14, 13 }) ?? double.NaN, 1e-9, "i persi vengono saltati");
            T.True(NetStats.Jitter(new double?[] { 10 }) == null, "un solo ping → null");
            T.True(NetStats.Jitter(new double?[] { null, null }) == null, "tutto perso → null");
            T.Near(0, NetStats.Jitter(Enumerable.Repeat<double?>(20, 50)) ?? double.NaN, 1e-12, "ping costante → 0");

            // RFC 3550: con |D| costante = 4 dopo n passi J = 4 × (1 − (15/16)^n).
            var series = Enumerable.Range(0, 11).Select(i => (double?)(i % 2 == 0 ? 10 : 14)).ToArray(); // 10 differenze
            T.Near(4 * (1 - Math.Pow(15.0 / 16, 10)), NetStats.JitterRfc3550(series) ?? double.NaN, 1e-9, "RFC 3550 livellato");
            T.True(NetStats.JitterRfc3550(new double?[] { 5 }) == null, "RFC 3550 con un solo valore");
        }

        private static void PercentileAndLoss()
        {
            var v = new double[] { 5, 1, 3, 2, 4 };
            T.Near(3, NetStats.Percentile(v, 50), 1e-12, "P50");
            T.Near(4.8, NetStats.Percentile(v, 95), 1e-12, "P95 interpolato");
            T.Near(1, NetStats.Percentile(v, 0), 1e-12, "P0");
            T.Near(5, NetStats.Percentile(v, 100), 1e-12, "P100");
            T.True(double.IsNaN(NetStats.Percentile(Array.Empty<double>(), 50)), "vuoto → NaN");
            T.Near(7, NetStats.Percentile(new double[] { 7 }, 95), 1e-12, "un valore");
            T.Near(2.5, NetStats.Median(new double[] { 1, 2, 3, 4 }), 1e-12, "mediana pari");
            T.Near(30, NetStats.LossPct(10, 7), 1e-12, "30% persi");
            T.Near(0, NetStats.LossPct(0, 0), 1e-12, "niente inviato → 0");
            T.Near(50, NetStats.LossPct(new double?[] { 1, null, 2, null }), 1e-12, "perdita da serie");
        }

        private static void SummarizeTests()
        {
            var s = NetStats.Summarize(new double?[] { 20, null, 22, 30, null }, "Server di gioco", "52.1.2.3");
            T.Equal(5, s.Sent, "Sent");
            T.Equal(3, s.Received, "Received");
            T.Near(40, s.LossPct, 1e-9, "LossPct");
            T.Near(24, s.AvgMs ?? double.NaN, 1e-9, "AvgMs");
            T.Near(20, s.MinMs ?? double.NaN, 1e-9, "MinMs");
            T.Near(30, s.MaxMs ?? double.NaN, 1e-9, "MaxMs");
            T.Near(30, s.LastMs ?? double.NaN, 1e-9, "LastMs = ultimo riuscito");
            T.Near(5, s.JitterMs ?? double.NaN, 1e-9, "JitterMs");
            T.Near(29.2, s.P95Ms ?? double.NaN, 1e-9, "P95Ms");
            T.Equal("Server di gioco", s.Target, "Target");
            T.Equal("52.1.2.3", s.Host, "Host");

            var lost = NetStats.Summarize(new double?[] { null, null, null });
            T.True(lost.AvgMs == null && lost.LastMs == null && lost.JitterMs == null, "tutto perso: nessuna media");
            T.Near(100, lost.LossPct, 1e-9, "tutto perso: 100%");

            var empty = NetStats.Summarize(Array.Empty<double?>());
            T.Equal(0, empty.Sent, "vuoto: Sent");
            T.Near(0, empty.LossPct, 1e-9, "vuoto: nessuna perdita");
        }

        private static void PublicAddresses()
        {
            foreach (var priv in new[] { "192.168.1.1", "10.0.0.5", "172.16.5.4", "172.31.255.1", "100.64.1.1", "127.0.0.1",
                         "169.254.3.3", "0.0.0.0", "224.0.0.251", "255.255.255.255", "fe80::1", "fd12:3456::1", "::1", "::ffff:192.168.1.1" })
                T.True(!NetStats.IsPublicAddress(IPAddress.Parse(priv)), "privato: " + priv);
            foreach (var pub in new[] { "8.8.8.8", "172.32.0.1", "52.1.2.3", "100.128.0.1", "2001:4860:4860::8888", "::ffff:52.1.2.3" })
                T.True(NetStats.IsPublicAddress(IPAddress.Parse(pub)), "pubblico: " + pub);
            T.True(!NetStats.IsPublicAddress(null), "null");
            T.Equal("52.1.2.3:7777", new NetEndpoint(IPAddress.Parse("52.1.2.3"), 7777).ToString(), "formato IPv4");
            T.Equal("[2001:db8::1]:9000", new NetEndpoint(IPAddress.Parse("2001:db8::1"), 9000).ToString(), "formato IPv6");
        }

        private static byte[] PayloadV4(int pid, int size, string daddr, string saddr, int dport, int sport)
        {
            var b = new byte[28];
            BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)pid);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)size);
            IPAddress.Parse(daddr).GetAddressBytes().CopyTo(b, 8);
            IPAddress.Parse(saddr).GetAddressBytes().CopyTo(b, 12);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(16), (ushort)dport);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(18), (ushort)sport);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20), 99);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(24), 7);
            return b;
        }

        private static void PayloadParsing()
        {
            // Ricezione IPv4: in un evento di ricezione daddr può essere l'indirizzo locale e saddr il server.
            var raw = PayloadV4(4321, 100, "192.168.1.10", "52.1.2.3", 54321, 7777);
            T.True(KernelNetPayload.TryParse(raw, false, out var ev), "IPv4 decodificato");
            T.Equal(4321, ev.Pid, "PID dal payload");
            T.Equal(100, ev.Size, "size");
            T.Equal("192.168.1.10", ev.DAddr.ToString(), "daddr in ordine di rete");
            T.Equal("52.1.2.3", ev.SAddr.ToString(), "saddr");
            T.Equal(54321, ev.DPort, "dport big-endian");
            T.Equal(7777, ev.SPort, "sport big-endian");

            var local = new HashSet<IPAddress> { IPAddress.Parse("192.168.1.10") };
            T.Equal("52.1.2.3:7777", KernelNetPayload.Remote(ev, isSend: false, local).ToString(), "remoto = non locale");
            T.Equal("52.1.2.3:7777", KernelNetPayload.Remote(ev, isSend: true, local).ToString(), "anche se l'evento fosse un invio");
            // Convenzione opposta (daddr remoto anche in ricezione): l'elenco degli indirizzi locali la risolve.
            var swapped = PayloadV4(4321, 100, "52.1.2.3", "192.168.1.10", 7777, 54321);
            KernelNetPayload.TryParse(swapped, false, out var ev2);
            T.Equal("52.1.2.3:7777", KernelNetPayload.Remote(ev2, isSend: false, local).ToString(), "convenzione opposta");
            // Senza indirizzi locali: daddr per gli invii, saddr per le ricezioni.
            T.Equal("52.1.2.3:7777", KernelNetPayload.Remote(ev, isSend: false, null).ToString(), "ripiego ricezione → saddr");
            T.Equal("192.168.1.10:54321", KernelNetPayload.Remote(ev, isSend: true, new HashSet<IPAddress>()).ToString(), "ripiego invio → daddr");

            // IPv6.
            var b6 = new byte[52];
            BinaryPrimitives.WriteUInt32LittleEndian(b6, 77);
            BinaryPrimitives.WriteUInt32LittleEndian(b6.AsSpan(4), 64);
            IPAddress.Parse("2001:db8::5").GetAddressBytes().CopyTo(b6, 8);
            IPAddress.Parse("2a01:4f8::1").GetAddressBytes().CopyTo(b6, 24);
            BinaryPrimitives.WriteUInt16BigEndian(b6.AsSpan(40), 9000);
            BinaryPrimitives.WriteUInt16BigEndian(b6.AsSpan(42), 50000);
            T.True(KernelNetPayload.TryParse(b6, true, out var e6), "IPv6 decodificato");
            T.Equal("2001:db8::5", e6.DAddr.ToString(), "daddr IPv6");
            T.Equal(9000, e6.DPort, "dport IPv6");
            T.Equal("[2001:db8::5]:9000", KernelNetPayload.Remote(e6, isSend: true, null).ToString(), "remoto IPv6 in invio");

            // Payload troncati o assurdi.
            T.True(!KernelNetPayload.TryParse(raw.AsSpan(0, 19), false, out _), "IPv4 troncato");
            T.True(!KernelNetPayload.TryParse(b6.AsSpan(0, 43), true, out _), "IPv6 troncato");
            T.True(!KernelNetPayload.TryParse(ReadOnlySpan<byte>.Empty, false, out _), "vuoto");
            var huge = PayloadV4(1, 50_000_000, "1.1.1.1", "2.2.2.2", 1, 1);
            T.True(!KernelNetPayload.TryParse(huge, false, out _), "dimensione assurda");
        }

        private static void FreezeDetection()
        {
            var d = new FreezeDetector();
            double t = 0;
            for (int i = 0; i < 120; i++) { t = i * 1000.0 / 60; d.OnPacket(t); } // 2 s a 60 pkt/s
            var (gap, freeze) = d.OnPacket(t + 400);
            T.Near(400, gap, 1e-9, "pausa misurata");
            T.True(freeze, "400 ms dopo 60 pkt/s → freeze");
            var (gap2, f2) = d.OnPacket(t + 400 + 16);
            T.True(!f2 && Math.Abs(gap2 - 16) < 1e-9, "pacchetto successivo normale");

            var slow = new FreezeDetector();
            for (int i = 0; i < 10; i++) slow.OnPacket(i * 200.0); // 5 pkt/s
            T.True(!slow.OnPacket(1800 + 300).Freeze, "flusso lento: nessun freeze");

            var shortGap = new FreezeDetector();
            for (int i = 0; i < 60; i++) shortGap.OnPacket(i * 1000.0 / 60);
            T.True(!shortGap.OnPacket(983.3 + 200).Freeze, "200 ms non è un freeze");
            T.True(double.IsNaN(new FreezeDetector().OnPacket(5).GapMs), "primo pacchetto: nessuna pausa");
            var ooo = new FreezeDetector();
            ooo.OnPacket(100);
            T.True(double.IsNaN(ooo.OnPacket(50).GapMs), "fuori ordine ignorato");
        }

        private static void Aggregator()
        {
            var server = new NetEndpoint(IPAddress.Parse("52.1.2.3"), 7777);
            var lan = new NetEndpoint(IPAddress.Parse("192.168.1.50"), 5000);
            var voice = new NetEndpoint(IPAddress.Parse("35.10.0.9"), 3478);
            var server2 = new NetEndpoint(IPAddress.Parse("35.0.0.1"), 7000);
            var packets = new List<NetPacket>();
            // 20 s: server a 60 pkt/s (pausa di ~600 ms a 10,2 s), invii a 30 pkt/s, LAN a 100 pkt/s, voce a 15 pkt/s con silenzi.
            for (int k = 0; k < 1200; k++)
            {
                double t = k * 1000.0 / 60;
                if (t >= 10200 && t < 10800) continue;
                packets.Add(new NetPacket(t, true, 128, server));
            }
            for (int k = 0; k < 600; k++) packets.Add(new NetPacket(k * 1000.0 / 30 + 3, false, 80, server));
            for (int k = 0; k < 2000; k++) packets.Add(new NetPacket(k * 10.0 + 1, true, 200, lan));
            for (int k = 0; k < 300; k++)
            {
                double t = k * 1000.0 / 15 + 5;
                if ((int)(t / 3000) % 2 == 1) continue; // silenzi di 3 s
                packets.Add(new NetPacket(t, true, 100, voice));
            }
            // Poi un nuovo server per 15 s (cambio partita), senza pausa tra i due.
            for (int k = 0; k < 900; k++) packets.Add(new NetPacket(20000 + k * 1000.0 / 60, true, 128, server2));

            // Ordine casuale (buffer ETW per CPU) e arrivo a blocchi.
            var rnd = new Random(42);
            var shuffled = packets.OrderBy(_ => rnd.Next()).ToList();
            var agg = new NetTrafficAggregator();
            var seconds = new List<NetSecondTraffic>();
            for (double now = 1000; now <= 37000; now += 1000)
            {
                // Arriva tutto ciò che è successo fino a "now"; si chiudono i secondi fino a now − 2 s.
                foreach (var p in shuffled.Where(p => p.TimeMs >= now - 1000 && p.TimeMs < now)) agg.Add(p);
                seconds.AddRange(agg.Advance(now - 2000));
            }
            seconds.AddRange(agg.Advance(40000));

            T.Equal(40, seconds.Count, "un elemento per secondo, senza buchi");
            T.Equal(packets.Count(p => p.Received), seconds.Sum(s => s.PacketsIn), "nessun pacchetto ricevuto perso");
            T.Equal(packets.Count(p => !p.Received), seconds.Sum(s => s.PacketsOut), "nessun pacchetto inviato perso");
            T.Equal(2, agg.ServersSeen.Count, "due server distinti");
            T.Equal(server, agg.ServersSeen[0], "primo server (non la LAN, anche se manda più pacchetti)");
            T.Equal(server2, agg.ServersSeen[1], "secondo server");
            T.Equal(1, seconds.Sum(s => s.Freezes), "un solo freeze (la voce con i silenzi e il cambio server non contano)");
            T.Equal(1, seconds[10].Freezes, "freeze nel secondo in cui inizia");
            T.Near(1000.0 / 60 * 648 - 1000.0 / 60 * 611, seconds.Max(s => s.LongestFreezeMs), 1e-6, "durata del freeze");
            T.True(seconds[10].MaxRecvGapMs is > 600, "pausa massima nel secondo in cui finisce");
            T.True(seconds[5].MaxRecvGapMs is > 16 and < 18, "pausa normale ~16,7 ms");
            T.Equal(server, seconds[5].Server, "server a metà del primo tratto");
            T.Equal(server2, seconds[30].Server, "server dopo il cambio");
            // Secondo 7: 60 dal server + 100 LAN + 15 voce = 175 ricevuti; banda in = (60×128 + 100×200 + 15×100) × 8 / 1000.
            T.Equal(175, seconds[7].PacketsIn, "pacchetti ricevuti al secondo");
            T.Near((60 * 128 + 100 * 200 + 15 * 100) * 8 / 1000.0, seconds[7].KbpsIn, 1e-9, "kbit/s in ingresso");
            T.Near(30 * 80 * 8 / 1000.0, seconds[7].KbpsOut, 1e-9, "kbit/s in uscita");
            T.Equal(1, agg.CountFreezesSince(0), "freeze recenti");
            T.Equal(0, agg.CountFreezesSince(20000), "nessun freeze dopo 20 s");

            // Pacchetti arrivati dopo la chiusura del loro secondo: contati nel primo secondo aperto, senza falsi freeze.
            var late = new NetTrafficAggregator();
            for (int k = 0; k < 120; k++) late.Add(new NetPacket(k * 1000.0 / 60, true, 100, server));
            var first = late.Advance(1000);
            late.Add(new NetPacket(500, true, 100, server));
            var next = late.Advance(2000);
            T.Equal(60, first[0].PacketsIn, "primo secondo");
            T.Equal(61, next[0].PacketsIn, "pacchetto in ritardo nel secondo successivo");
            T.Equal(0, next[0].Freezes, "nessun freeze per i ritardatari");

            // Pausa lunghissima (PC sospeso): niente migliaia di secondi vuoti.
            var sleep = new NetTrafficAggregator();
            T.True(sleep.Advance(10_000_000).Count <= 300, "recupero limitato dopo una sospensione");
        }

        private static void Correlation()
        {
            var secs = Enumerable.Range(0, 60).Select(i => new SecondSample { T = i }).ToList();
            secs[5].NetFreezes = 1;
            secs[20].NetFreezes = 2;
            secs[6].Stutters = 1;
            secs[40].Stutters = 3;
            secs[41].Stutters = 1;
            var c = NetStats.Correlate(secs);
            T.Equal(2, c.FreezeSeconds, "secondi con freeze");
            T.Equal(3, c.StutterSeconds, "secondi con stutter");
            T.Equal(1, c.FreezeWithStutter, "freeze vicino a uno stutter (±1 s)");
            T.Equal(1, c.StutterWithFreeze, "stutter vicino a un freeze");
            T.Equal(3, NetStats.TotalFreezes(secs), "freeze totali");
            T.Equal(0, NetStats.Correlate(null).FreezeSeconds, "null");
        }

        private static void DisplayHelpers()
        {
            T.Equal("Europa", NetStats.RegionDisplayName(PingRegion.Europe), "Europa");
            T.Equal("Medio Oriente", NetStats.RegionDisplayName(PingRegion.MiddleEast), "Medio Oriente");
            T.Equal("Auto", NetStats.RegionDisplayName(PingRegion.Auto), "Auto");
            foreach (PingRegion r in Enum.GetValues(typeof(PingRegion)))
                T.True(r == PingRegion.Auto || NetStats.RegionDisplayName(r) != "Auto", "ogni regione ha un nome: " + r);

            T.Equal(0, NetDisplay.PingLevel(79.9), "ping 79,9 → ok");
            T.Equal(1, NetDisplay.PingLevel(80), "ping 80 → attenzione");
            T.Equal(2, NetDisplay.PingLevel(120), "ping 120 → problema");
            T.Equal(0, NetDisplay.PingLevel(null), "ping assente → nessun colore");
            T.Equal(1, NetDisplay.JitterLevel(10), "jitter 10 → attenzione");
            T.Equal(2, NetDisplay.JitterLevel(25), "jitter 25 → problema");
            T.Equal(0, NetDisplay.LossLevel(0.5), "perdita 0,5% → ok");
            T.Equal(1, NetDisplay.LossLevel(1), "perdita 1% → attenzione");
            T.Equal(2, NetDisplay.LossLevel(3), "perdita 3% → problema");

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var p = new PingStats { AvgMs = 24.4, JitterMs = 2.2, LossPct = 0, Sent = 60, Received = 60 };
            T.Equal("Ping 24 ms · jitter 2 · perdita 0%", NetDisplay.PingLine(p, inv), "riga del ping");
            T.Equal(0, NetDisplay.Level(p), "livello complessivo buono");
            p.LossPct = 3.3;
            T.Equal("Ping 24 ms · jitter 2 · perdita 3.3%", NetDisplay.PingLine(p, inv), "perdita con un decimale");
            T.Equal(2, NetDisplay.Level(p), "la perdita peggiora il livello complessivo");
            T.Equal("Ping: n/d", NetDisplay.PingLine(null, inv), "senza dati");
            T.Equal("Ping: n/d", NetDisplay.PingLine(new PingStats { Sent = 5 }, inv), "nessun ping riuscito");
            T.Equal("Ping 30 ms · jitter – · perdita 0%", NetDisplay.PingLine(new PingStats { LastMs = 30 }, inv), "solo l'ultimo ping");
        }

        private static void WifiParsing()
        {
            const string en = @"
There are 2 interfaces on the system:

    Name                   : Wi-Fi 2
    Description            : USB Wireless LAN Card
    GUID                   : 11111111-2222-3333-4444-555555555555
    State                  : disconnected

    Name                   : Wi-Fi
    Description            : Intel(R) Wi-Fi 6 AX201 160MHz
    GUID                   : 66666666-2222-3333-4444-555555555555
    Physical address       : aa:bb:cc:dd:ee:ff
    State                  : connected
    SSID                   : 99%
    Radio type             : 802.11ax
    Receive rate (Mbps)    : 1201
    Signal                 : 81%
    Profile                : Casa

    Hosted network status  : Not available
";
            T.Equal(81, NetStats.ParseWifiSignal(en, "Wi-Fi", "Intel(R) Wi-Fi 6 AX201 160MHz") ?? -1, "inglese, interfaccia giusta");
            T.Equal(81, NetStats.ParseWifiSignal(en, "nome sconosciuto", "Intel(R) Wi-Fi 6 AX201 160MHz") ?? -1, "trovata dalla descrizione");
            T.True(NetStats.ParseWifiSignal(en, "Wi-Fi 2", "USB Wireless LAN Card") == null, "interfaccia scollegata: nessun segnale");

            const string it = "Nell'interfaccia di sistema è presente 1 interfaccia:\r\n\r\n    Nome                   : Wi-Fi\r\n" +
                              "    Descrizione            : Realtek 8822CE\r\n    Stato                  : connessa\r\n    Segnale                : 72%\r\n";
            T.Equal(72, NetStats.ParseWifiSignal(it, "Wi-Fi", "Realtek 8822CE") ?? -1, "italiano");
            const string fr = "    Nom                    : Wi-Fi\n    Description            : Killer AX1650\n    Signal                 : 85 %\n";
            T.Equal(85, NetStats.ParseWifiSignal(fr, "Ethernet", "altro") ?? -1, "francese, unico blocco con %");
            // Blocchi senza riga vuota in mezzo: separati dal ripetersi della prima chiave.
            const string glued = "    Name : A\n    Description : Uno\n    Signal : 40%\n    Name : B\n    Description : Due\n    Signal : 90%\n";
            T.Equal(90, NetStats.ParseWifiSignal(glued, "B", null) ?? -1, "blocchi attaccati");
            T.True(NetStats.ParseWifiSignal("", "Wi-Fi", null) == null, "output vuoto");
            T.True(NetStats.ParseWifiSignal("Il servizio WLAN AutoConfig non è in esecuzione.", "Wi-Fi", null) == null, "servizio spento");
        }

        private static void ProcessUsageTests()
        {
            T.Equal("chrome", ProcessNames.Normalize("chrome#12"), "istanza #n");
            T.Equal("a#b", ProcessNames.Normalize("a#b"), "# non numerico");
            T.Equal("x#", ProcessNames.Normalize("x#"), "# finale");
            foreach (var ex in new[] { "_Total", "Idle", "FNBoost", Fn, "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService_x64", "BattlEye_Launcher",
                         "FortniteClient-Win64-Shipping_EAC_EOS" })
                T.True(ProcessNames.IsExcluded(ex, "FNBoost", Fn), "escluso: " + ex);
            T.True(ProcessNames.IsExcluded("MyGame", "FNBoost", "MyGame"), "gioco in primo piano escluso");
            T.True(!ProcessNames.IsExcluded("chrome", "FNBoost", Fn), "chrome incluso");

            var acc = new ProcessUsageAccumulator();
            acc.Add(new Dictionary<string, (double, double)> { ["chrome"] = (10, 800), ["tiny"] = (0.2, 50), ["bigram"] = (0, 600), ["once"] = (8, 10) });
            acc.Add(new Dictionary<string, (double, double)> { ["chrome"] = (20, 1000), ["tiny"] = (0.2, 50), ["bigram"] = (0.1, 600) });
            var top = acc.Top();
            T.Equal(2, acc.Samples, "campioni");
            T.Equal("chrome", top[0].Name, "primo per CPU");
            T.Near(15, top[0].AvgCpuPct, 1e-9, "CPU media");
            T.Near(20, top[0].MaxCpuPct, 1e-9, "CPU massima");
            T.Near(900, top[0].AvgRamMb, 1e-9, "RAM media");
            T.True(top.Any(p => p.Name == "once" && Math.Abs(p.AvgCpuPct - 4) < 1e-9), "assente in un campione conta 0 → media 4%");
            T.True(top.Any(p => p.Name == "bigram"), "molta RAM anche con poca CPU");
            T.True(!top.Any(p => p.Name == "tiny"), "processo trascurabile escluso");
            T.Equal(1, acc.Top(1).Count, "limite al numero richiesto");
            T.Equal(0, new ProcessUsageAccumulator().Top().Count, "nessun campione");
        }

        // ================= PerfAnalyzer =================

        private static void AnalyzerHighPing()
        {
            var s = Make("hp", Day(1), 144, 300);
            s.Network = Net(game: NetStats.Summarize(Series(300, 128, 132, 130, 135), "Server di gioco", "52.1.2.3"));
            s.Network.BestRegionName = "Europa";
            s.Network.BestRegionPingMs = 32;
            var ins = PerfAnalyzer.Analyze(s, Array.Empty<PerfSession>());
            var p = Get(ins, "Ping alto");
            T.True(p != null && p.Severity == CheckStatus.Bad, "ping > 120 → Bad");
            T.Contains(p?.Message ?? "", "Ping medio 131 ms verso il server di gioco", "valori nel messaggio");
            T.Contains(p?.Hint ?? "", "Regione matchmaking", "suggerisce la regione di matchmaking");
            T.Contains(p?.Hint ?? "", "Europa", "nomina la regione più vicina");
            T.True(!Has(ins, "Rete di casa instabile"), "router sano: nessun allarme sulla rete di casa");

            var w = Make("hp2", Day(1), 144, 300);
            w.Network = Net(game: NetStats.Summarize(Series(300, 88, 92), "Server di gioco", "52.1.2.3"));
            w.Network.Internet = NetStats.Summarize(Series(300, 85, 87));
            var insW = PerfAnalyzer.Analyze(w, Array.Empty<PerfSession>());
            var pw = Get(insW, "Ping alto");
            T.True(pw != null && pw.Severity == CheckStatus.Warn, "80-120 ms → Warn");
            T.Contains(pw?.Hint ?? "", "provider", "anche Internet lento → linea/provider");

            var ok = Make("hp3", Day(1), 144, 300);
            ok.Network = Net(game: NetStats.Summarize(Series(300, 30, 31), "Server di gioco", "52.1.2.3"));
            var insOk = PerfAnalyzer.Analyze(ok, Array.Empty<PerfSession>());
            T.True(Get(insOk, "Ping buono")?.Severity == CheckStatus.Ok, "ping basso → Ok");
            T.True(!Has(insOk, "Ping instabile (jitter)") && !Has(insOk, "Perdita di pacchetti"), "nessun falso allarme");

            // Ping misurato sulla regione (server muto all'ICMP) e regione impostata lontana.
            var reg = Make("hp4", Day(1), 144, 300);
            reg.Network = Net(game: NetStats.Summarize(Series(300, 95, 97), "Regione NA Est", "ping-nae.ds.on.epicgames.com"), kind: "regione");
            reg.Network.RegionName = "NA Est";
            reg.Network.BestRegionName = "Europa";
            reg.Network.BestRegionPingMs = 25;
            var pr = Get(PerfAnalyzer.Analyze(reg, Array.Empty<PerfSession>()), "Ping alto");
            T.Contains(pr?.Message ?? "", "non risponde al ping", "onestà: valore della regione");
            T.Contains(pr?.Hint ?? "", "FN Boost", "regione sbagliata nelle impostazioni");
        }

        private static void AnalyzerHomeNetwork()
        {
            var s = Make("wifi", Day(1), 144, 300);
            s.Network = Net(game: NetStats.Summarize(Series(300, 30, 50), "Server di gioco", "52.1.2.3"),
                gateway: NetStats.Summarize(Series(300, 2, 15), "Router", "router"));
            s.Network.ConnectionType = "Wi-Fi";
            s.Network.WifiSignalPct = 45;
            var ins = PerfAnalyzer.Analyze(s, Array.Empty<PerfSession>());
            var home = Get(ins, "Rete di casa instabile");
            T.True(home != null && home.Severity == CheckStatus.Warn, "router instabile → Warn");
            T.Contains(home?.Hint ?? "", "5 GHz", "consiglio Wi-Fi 5 GHz");
            T.Contains(home?.Hint ?? "", "cavo", "consiglio cavo");
            T.True(!(home?.Message ?? "").Contains("192.168", StringComparison.Ordinal), "nessun IP privato nel testo");
            T.True(Has(ins, "Segnale Wi-Fi debole"), "segnale < 60%");
            var jit = Get(ins, "Ping instabile (jitter)");
            T.True(jit != null && jit.Severity == CheckStatus.Warn, "jitter 20 ms → Warn");
            T.Contains(jit?.Hint ?? "", "router", "l'instabilità parte dal router");

            var cable = Make("eth", Day(1), 144, 300);
            cable.Network = Net(game: NetStats.Summarize(Series(300, 20, 50), "Server di gioco", "52.1.2.3"),
                gateway: NetStats.Summarize(Series(300, 1, 1.4), "Router", "router"));
            cable.Network.ConnectionType = "Ethernet";
            var insC = PerfAnalyzer.Analyze(cable, Array.Empty<PerfSession>());
            var jc = Get(insC, "Ping instabile (jitter)");
            T.True(jc != null && jc.Severity == CheckStatus.Bad, "jitter 30 ms → Bad");
            T.Contains(jc?.Hint ?? "", "fuori casa", "router stabile: il problema è fuori");
            T.True(!Has(insC, "Rete di casa instabile") && !Has(insC, "Segnale Wi-Fi debole"), "cavo e router sani");

            // Due ping persi verso il router bastano a segnalarlo, uno solo no.
            var lossy = Make("gwloss", Day(1), 144, 300);
            var gwSeries = Series(300, 1, 1.2).ToList();
            gwSeries[10] = null;
            lossy.Network = Net(game: NetStats.Summarize(Series(300, 30, 31)), gateway: NetStats.Summarize(gwSeries, "Router", "router"));
            T.True(!Has(PerfAnalyzer.Analyze(lossy, Array.Empty<PerfSession>()), "Rete di casa instabile"), "un solo ping perso: nessun allarme");
            gwSeries[20] = null;
            lossy.Network.Gateway = NetStats.Summarize(gwSeries, "Router", "router");
            T.True(Has(PerfAnalyzer.Analyze(lossy, Array.Empty<PerfSession>()), "Rete di casa instabile"), "due ping persi: allarme");
        }

        private static void AnalyzerLoss()
        {
            var s = Make("loss", Day(1), 144, 300);
            var series = Series(300, 30, 31).ToList();
            for (int i = 0; i < 15; i++) series[i * 20] = null; // 5%
            s.Network = Net(game: NetStats.Summarize(series, "Server di gioco", "52.1.2.3"),
                gateway: NetStats.Summarize(Series(300, 1, 1.2), "Router", "router"));
            var l = Get(PerfAnalyzer.Analyze(s, Array.Empty<PerfSession>()), "Perdita di pacchetti");
            T.True(l != null && l.Severity == CheckStatus.Bad, "5% → Bad");
            T.Contains(l?.Message ?? "", "15 su 300", "conteggio");
            T.Contains(l?.Hint ?? "", "limita le risposte al ping", "onestà sull'ICMP");

            var w = Make("loss2", Day(1), 144, 300);
            var s2 = Series(300, 30, 31).ToList();
            for (int i = 0; i < 6; i++) s2[i * 40] = null; // 2%
            w.Network = Net(game: NetStats.Summarize(s2));
            T.True(Get(PerfAnalyzer.Analyze(w, Array.Empty<PerfSession>()), "Perdita di pacchetti")?.Severity == CheckStatus.Warn, "2% → Warn");

            // Report: osservazione della sessione e controllo di rete descrivono la stessa perdita → una sola voce.
            var d = new FNBoost.Report.ReportData { Session = w, Insights = PerfAnalyzer.Analyze(w, Array.Empty<PerfSession>()).ToList() };
            var recs = FNBoost.Report.ReportBuilder.BuildRecommendations(d);
            T.Equal(1, recs.Count(r => r.Title.Contains("pacchetti", StringComparison.OrdinalIgnoreCase)), "perdita di pacchetti non ripetuta nel report");
        }

        private static void AnalyzerBackgroundBandwidth()
        {
            var s = Make("bw", Day(1), 144, 300);
            s.Network = Net(game: NetStats.Summarize(Series(300, 30, 31)));
            s.Network.AvgOtherAppsKbps = 3500;
            s.Network.MaxOtherAppsKbps = 8000;
            s.Network.AvgGameKbpsIn = 120;
            s.Network.AvgGameKbpsOut = 60;
            var b = Get(PerfAnalyzer.Analyze(s, Array.Empty<PerfSession>()), "Altre app usano la connessione");
            T.True(b != null && b.Severity == CheckStatus.Warn, "media > 2 Mbit/s");
            T.Contains(b?.Message ?? "", "3,5 Mbit/s", "media in Mbit/s");
            T.Contains(b?.Hint ?? "", "Windows Update", "cause tipiche");

            s.Network.AvgOtherAppsKbps = 500;
            s.Network.MaxOtherAppsKbps = 12000;
            T.True(Has(PerfAnalyzer.Analyze(s, Array.Empty<PerfSession>()), "Altre app usano la connessione"), "picco > 10 Mbit/s");
            s.Network.MaxOtherAppsKbps = 3000;
            T.True(!Has(PerfAnalyzer.Analyze(s, Array.Empty<PerfSession>()), "Altre app usano la connessione"), "traffico normale");
        }

        private static void AnalyzerFreezesVsStutters()
        {
            // Rete: 6 freeze, frame regolari → domina la rete.
            var s = Make("frz", Day(1), 144, 300);
            s.Network = Net(game: NetStats.Summarize(Series(300, 30, 31)));
            foreach (var k in new[] { 30, 120, 200, 250 }) s.Seconds[k].NetFreezes = 1;
            s.Network.Freezes = 4;
            s.Network.LongestFreezeMs = 1400;
            s.Network.AvgPacketsInPerSec = 60;
            var f = Get(PerfAnalyzer.Analyze(s, Array.Empty<PerfSession>()), "Freeze di rete (lag)");
            T.True(f != null && f.Severity == CheckStatus.Warn, "4 freeze in 5 minuti → Warn");
            T.Contains(f?.Message ?? "", "pesano più degli scatti", "domina la rete");
            T.Contains(f?.Message ?? "", "1400 ms", "freeze più lungo");

            // Molti stutter dei frame e un solo freeze → priorità al PC.
            var ft = Const(144, 300);
            for (int i = 0; i < 40; i++) ft[500 + i * 1000] = 60f;
            var pc = Make("pc", Day(1), ft);
            pc.Network = Net(game: NetStats.Summarize(Series(300, 30, 31)));
            pc.Seconds[150].NetFreezes = 1;
            pc.Network.Freezes = 1;
            pc.Network.LongestFreezeMs = 300;
            var fp = Get(PerfAnalyzer.Analyze(pc, Array.Empty<PerfSession>()), "Freeze di rete (lag)");
            T.True(fp != null && fp.Severity == CheckStatus.Info, "un freeze breve → Info");
            T.Contains(fp?.Message ?? "", "la priorità è il PC", "dominano gli stutter");

            // Freeze che coincidono con gli stutter → possibile blocco del PC.
            var co = Make("co", Day(1), ft);
            co.Network = Net(game: NetStats.Summarize(Series(300, 30, 31)));
            int marked = 0;
            for (int k = 0; k < co.Seconds.Count && marked < 30; k++)
                if (co.Seconds[k].Stutters > 0) { co.Seconds[k].NetFreezes = 1; marked++; }
            co.Network.Freezes = marked;
            co.Network.LongestFreezeMs = 2500;
            var fc = Get(PerfAnalyzer.Analyze(co, Array.Empty<PerfSession>()), "Freeze di rete (lag)");
            T.True(fc != null && fc.Severity == CheckStatus.Bad, "freeze di 2,5 s → Bad");
            T.Contains(fc?.Message ?? "", "blocco dell'intero PC", "coincidenza segnalata");

            // Nessun freeze ma molti stutter → gli scatti vengono dal PC.
            var none = Make("none", Day(1), ft);
            none.Network = Net(game: NetStats.Summarize(Series(300, 30, 31)));
            none.Network.AvgPacketsInPerSec = 60;
            var insN = PerfAnalyzer.Analyze(none, Array.Empty<PerfSession>());
            T.True(Has(insN, "Nessun freeze di rete"), "connessione pulita, stutter dal PC");
            T.True(!Has(insN, "Freeze di rete (lag)"), "nessun freeze");

            // Pochi pacchetti dal server.
            var slow = Make("slowpk", Day(1), 144, 300);
            slow.Network = Net(game: NetStats.Summarize(Series(300, 30, 31)));
            foreach (var x in slow.Seconds) x.PacketsInPerSec = 12;
            T.True(Has(PerfAnalyzer.Analyze(slow, Array.Empty<PerfSession>()), "Pochi aggiornamenti dal server"), "< 20 pkt/s");
            foreach (var x in slow.Seconds) x.PacketsInPerSec = 60;
            T.True(!Has(PerfAnalyzer.Analyze(slow, Array.Empty<PerfSession>()), "Pochi aggiornamenti dal server"), "60 pkt/s: normale");
        }

        private static void AnalyzerProcesses()
        {
            var s = Make("proc", Day(1), 144, 300);
            s.TopProcesses = new List<ProcessUsage>
            {
                new() { Name = "chrome", AvgCpuPct = 12.4, MaxCpuPct = 40, AvgRamMb = 2048 },
                new() { Name = "Discord", AvgCpuPct = 2, MaxCpuPct = 6, AvgRamMb = 400 }
            };
            var p = Get(PerfAnalyzer.Analyze(s, Array.Empty<PerfSession>()), "Programmi in background pesanti");
            T.True(p != null && p.Severity == CheckStatus.Warn, "processo > 5% CPU");
            T.Contains(p?.Message ?? "", "chrome (12% CPU in media, picco 40%, 2 GB di RAM)", "nome e valori");
            T.True(!(p?.Message ?? "").Contains("Discord", StringComparison.Ordinal), "sotto il 5% non viene nominato");
            s.TopProcesses[0].AvgCpuPct = 3;
            T.True(!Has(PerfAnalyzer.Analyze(s, Array.Empty<PerfSession>()), "Programmi in background pesanti"), "niente di pesante");
        }

        private static void AnalyzerNetComparison()
        {
            var prev = Make("p", Day(1), 144, 120);
            prev.Network = Net(game: NetStats.Summarize(Series(120, 30, 31)));
            var cur = Make("c", Day(2), 144, 120);
            cur.Network = Net(game: NetStats.Summarize(Series(120, 58, 62)));
            var cmp = PerfAnalyzer.Analyze(cur, new[] { prev, cur })
                .FirstOrDefault(i => i.Title.EndsWith("sessione precedente", StringComparison.Ordinal));
            T.True(cmp != null, "confronto presente");
            T.Contains(cmp?.Message ?? "", "Rete: ping 30 → 60 ms, jitter 1 → 4 ms, perdita 0 → 0%.", "delta di rete");

            var old = Make("o", Day(0), 144, 120); // senza rete
            var cmp2 = PerfAnalyzer.Analyze(prev, new[] { old, prev })
                .FirstOrDefault(i => i.Title.EndsWith("sessione precedente", StringComparison.Ordinal));
            T.True(cmp2 != null && !cmp2.Message.Contains("Rete:", StringComparison.Ordinal), "precedente senza rete: nessun delta");
        }

        private static void AnalyzerWithoutNetwork()
        {
            var s = Make("old", Day(1), 144, 300);
            var ins = PerfAnalyzer.Analyze(s, Array.Empty<PerfSession>());
            var netTitles = new[] { "Ping alto", "Ping buono", "Ping instabile (jitter)", "Perdita di pacchetti", "Rete di casa instabile",
                "Segnale Wi-Fi debole", "Altre app usano la connessione", "Freeze di rete (lag)", "Nessun freeze di rete",
                "Pochi aggiornamenti dal server", "Programmi in background pesanti" };
            T.True(!ins.Any(i => netTitles.Contains(i.Title)), "sessione senza rete: nessuna osservazione di rete");
            T.True(Has(ins, "Gioco fluido"), "l'analisi FPS resta invariata");

            // Rete misurata ma senza ping (es. rete assente): nessun errore, nessun ping.
            var noPing = Make("np", Day(1), 144, 300);
            noPing.Network = new NetworkSummary();
            noPing.TopProcesses = null!;
            var ins2 = PerfAnalyzer.Analyze(noPing, Array.Empty<PerfSession>());
            T.True(!Has(ins2, "Ping alto") && !Has(ins2, "Ping buono"), "nessun ping, nessuna osservazione");

            // Sessione JSON vecchia (senza i campi nuovi) e deserializzata: i campi di rete sono null.
            var json = "{\"Id\":\"x\",\"StartedAt\":\"2026-03-01T20:00:00\",\"ProcessName\":\"" + Fn + "\",\"Seconds\":[{\"T\":0,\"Fps\":144}]}";
            var parsed = System.Text.Json.JsonSerializer.Deserialize<PerfSession>(json)!;
            T.True(parsed.Network == null && parsed.Seconds[0].PingMs == null && parsed.TopProcesses.Count == 0, "JSON vecchio compatibile");
        }

        // ================= utilità =================

        private static DateTime Day(int d) => new DateTime(2026, 3, 1, 20, 0, 0).AddDays(d);

        private static float[] Const(double fps, double seconds) =>
            Enumerable.Repeat((float)(1000.0 / fps), (int)Math.Round(fps * seconds)).ToArray();

        /// <summary>Serie di n ping che ripete i valori indicati.</summary>
        private static double?[] Series(int n, params double[] values) =>
            Enumerable.Range(0, n).Select(i => (double?)values[i % values.Length]).ToArray();

        private static NetworkSummary Net(PingStats? game = null, PingStats? gateway = null, string kind = "server") => new()
        {
            PingTargetKind = kind,
            RegionName = "Europa",
            Game = game,
            Gateway = gateway ?? NetStats.Summarize(Series(game?.Sent ?? 60, 1, 1.3), "Router", "router"),
            Internet = NetStats.Summarize(Series(game?.Sent ?? 60, 12, 13)),
            ConnectionType = "Ethernet",
            ServerEndpoints = new List<string> { "52.1.2.3:7777" }
        };

        private static PerfSession Make(string id, DateTime at, double fps, double seconds) => Make(id, at, Const(fps, seconds));

        /// <summary>Sessione sintetica con statistiche e campioni al secondo coerenti con i frametime.</summary>
        private static PerfSession Make(string id, DateTime at, float[] ft)
        {
            var s = new PerfSession { Id = id, StartedAt = at, ProcessName = Fn, Stats = FrameStats.Compute(ft) };
            s.DurationSec = s.Stats.DurationSec;
            var flags = FrameStats.StutterFlags(ft, 2.5, 12);
            var fps = FrameStats.PerSecondFps(ft);
            var stutters = new int[fps.Length];
            double t = 0;
            for (int i = 0; i < ft.Length; i++)
            {
                t += ft[i];
                int k = Math.Max(0, (int)Math.Ceiling(t / 1000.0) - 1);
                if (flags[i] && k < stutters.Length) stutters[k]++;
            }
            for (int k = 0; k < fps.Length; k++)
                s.Seconds.Add(new SecondSample { T = k, Fps = fps[k], Stutters = stutters[k], CpuPercent = 30, RamPercent = 50 });
            return s;
        }

        private static PerfInsight? Get(List<PerfInsight> list, string title) => list.FirstOrDefault(i => i.Title == title);
        private static bool Has(List<PerfInsight> list, string title) => Get(list, title) != null;
    }
}
