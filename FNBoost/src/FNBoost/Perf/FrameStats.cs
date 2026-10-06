using System;
using System.Collections.Generic;

// Questo file non dipende da WPF: viene compilato anche dal progetto di test su Linux.

namespace FNBoost.Perf
{
    /// <summary>
    /// Statistiche sui frametime (ms), con le stesse definizioni usate da PresentMon / CapFrameX.
    /// <list type="bullet">
    /// <item><b>AvgFps</b> = frame / somma(frametime) × 1000 (media "vera", non la media degli FPS istantanei).</item>
    /// <item><b>Low1Fps</b> = 1000 / media dell'1% dei frametime più lunghi (almeno 1 frame, arrotondato per difetto).</item>
    /// <item><b>Low01Fps</b> = come sopra con lo 0,1%.</item>
    /// <item><b>P1Fps</b> = 1000 / 99° percentile dei frametime (interpolazione lineare tra i ranghi).</item>
    /// <item><b>MinFps</b> = 1000 / frametime massimo; <b>MaxFps</b> = 1000 / frametime minimo.</item>
    /// <item><b>Mediana, P99, deviazione standard</b> (di popolazione) sui frametime.</item>
    /// <item><b>Stutter</b>: frame con ft ≥ fattore × mediana dei 60 frame precedenti (finestra mobile,
    /// robusta ai picchi) <i>e</i> ft ≥ soglia minima in ms.</item>
    /// <item><b>ConsistencyScore</b> (0-100) = punteggio "rapporto" − penalità stutter, dove
    /// punteggio rapporto = clamp((Low1/Avg − 0,30) / 0,65, 0, 1) × 100 (rapporto ≥ 0,95 → 100, ≤ 0,30 → 0)
    /// e penalità = 30 × (1 − e^(−stutter al minuto / 10)) (max 30 punti).</item>
    /// </list>
    /// Valori NaN, infiniti, negativi o zero vengono ignorati.
    /// </summary>
    public static class FrameStats
    {
        /// <summary>Numero di frame precedenti usati per la mediana locale dello stutter.</summary>
        public const int StutterWindow = 60;

        public static FrameStatsResult Compute(IReadOnlyList<float> frametimesMs, double stutterFactor = 2.5, double stutterMinMs = 12)
        {
            var ft = Clean(frametimesMs);
            int n = ft.Length;
            if (n < 2) return new FrameStatsResult();

            double sum = 0, min = double.MaxValue, max = 0;
            foreach (var f in ft)
            {
                sum += f;
                if (f < min) min = f;
                if (f > max) max = f;
            }
            double mean = sum / n;
            double var = 0;
            foreach (var f in ft)
            {
                var d = f - mean;
                var += d * d;
            }

            var sorted = (float[])ft.Clone();
            Array.Sort(sorted);

            int stutters = CountStutters(ft, stutterFactor, stutterMinMs);
            double durationSec = sum / 1000.0;
            double avgFps = n * 1000.0 / sum;
            double low1 = 1000.0 / MeanOfSlowest(sorted, 0.01);
            double perMin = durationSec > 0 ? stutters / (durationSec / 60.0) : 0;

            return new FrameStatsResult
            {
                Frames = n,
                DurationSec = durationSec,
                AvgFps = avgFps,
                Low1Fps = low1,
                Low01Fps = 1000.0 / MeanOfSlowest(sorted, 0.001),
                P1Fps = 1000.0 / Percentile(sorted, 0.99),
                MinFps = 1000.0 / max,
                MaxFps = 1000.0 / min,
                AvgFrametimeMs = mean,
                MedianFrametimeMs = Percentile(sorted, 0.5),
                P99FrametimeMs = Percentile(sorted, 0.99),
                MaxFrametimeMs = max,
                StdDevFrametimeMs = Math.Sqrt(var / n),
                Stutters = stutters,
                StuttersPerMin = perMin,
                ConsistencyScore = Consistency(low1 / avgFps, perMin)
            };
        }

        /// <summary>true se il frame è uno stutter: ft ≥ factor × mediana locale e ft ≥ minMs.</summary>
        public static bool IsStutter(float frametimeMs, double localMedianMs, double factor, double minMs) =>
            IsValid(frametimeMs) && localMedianMs > 0 && frametimeMs >= factor * localMedianMs && frametimeMs >= minMs;

        /// <summary>
        /// FPS di ogni secondo consecutivo, sommando i frametime: un frame appartiene al secondo in cui termina.
        /// L'ultimo secondo parziale viene incluso (riscalato) solo se dura almeno 0,5 s.
        /// Un frame lunghissimo produce secondi a 0 FPS.
        /// </summary>
        public static double[] PerSecondFps(IReadOnlyList<float> frametimesMs)
        {
            var result = new List<double>();
            if (frametimesMs == null) return Array.Empty<double>();
            double t = 0, bucketStart = 0;
            int count = 0;
            for (int i = 0; i < frametimesMs.Count; i++)
            {
                var f = frametimesMs[i];
                if (!IsValid(f)) continue;
                t += f;
                while (t > bucketStart + 1000.0)
                {
                    result.Add(count);
                    count = 0;
                    bucketStart += 1000.0;
                }
                count++;
            }
            var rest = t - bucketStart;
            if (rest >= 500.0 && count > 0) result.Add(count * 1000.0 / rest);
            return result.ToArray();
        }

        /// <summary>
        /// Indica per ogni frame (già ripulito) se è uno stutter, con la mediana dei <see cref="StutterWindow"/>
        /// frame precedenti. Finestra ordinata mantenuta con ricerca binaria: O(n · finestra), finestra costante.
        /// </summary>
        internal static bool[] StutterFlags(IReadOnlyList<float> ft, double factor, double minMs)
        {
            var flags = new bool[ft.Count];
            var window = new float[StutterWindow]; // ordinata
            var ring = new float[StutterWindow];   // in ordine di arrivo, per sapere chi esce
            int wCount = 0, ringPos = 0;
            for (int i = 0; i < ft.Count; i++)
            {
                var f = ft[i];
                if (!IsValid(f)) continue;
                if (wCount > 0)
                {
                    double median = (wCount & 1) == 1
                        ? window[wCount / 2]
                        : (window[wCount / 2 - 1] + window[wCount / 2]) / 2.0;
                    flags[i] = IsStutter(f, median, factor, minMs);
                }
                if (wCount == StutterWindow)
                {
                    // Togli il frame più vecchio dalla finestra ordinata.
                    int idx = Array.BinarySearch(window, 0, wCount, ring[ringPos]);
                    if (idx < 0) idx = Math.Min(~idx, wCount - 1);
                    Array.Copy(window, idx + 1, window, idx, wCount - idx - 1);
                    wCount--;
                }
                ring[ringPos] = f;
                ringPos = (ringPos + 1) % StutterWindow;
                int pos = Array.BinarySearch(window, 0, wCount, f);
                if (pos < 0) pos = ~pos;
                Array.Copy(window, pos, window, pos + 1, wCount - pos);
                window[pos] = f;
                wCount++;
            }
            return flags;
        }

        /// <summary>FPS "low" di una porzione: 1000 / media della frazione più lenta (almeno 1 frame).</summary>
        internal static double LowFps(IReadOnlyList<float> frametimesMs, double fraction)
        {
            var ft = Clean(frametimesMs);
            if (ft.Length == 0) return 0;
            Array.Sort(ft);
            return 1000.0 / MeanOfSlowest(ft, fraction);
        }

        internal static bool IsValid(float f) => f > 0 && !float.IsNaN(f) && !float.IsInfinity(f);

        private static float[] Clean(IReadOnlyList<float>? input)
        {
            if (input == null || input.Count == 0) return Array.Empty<float>();
            int valid = 0;
            for (int i = 0; i < input.Count; i++) if (IsValid(input[i])) valid++;
            var arr = new float[valid];
            int k = 0;
            for (int i = 0; i < input.Count; i++) if (IsValid(input[i])) arr[k++] = input[i];
            return arr;
        }

        private static int CountStutters(float[] ft, double factor, double minMs)
        {
            int c = 0;
            foreach (var s in StutterFlags(ft, factor, minMs)) if (s) c++;
            return c;
        }

        /// <summary>Media dei frametime più lunghi (array ordinato crescente).</summary>
        private static double MeanOfSlowest(float[] sorted, double fraction)
        {
            int n = sorted.Length;
            int k = Math.Max(1, (int)Math.Floor(n * fraction));
            double s = 0;
            for (int i = n - k; i < n; i++) s += sorted[i];
            return s / k;
        }

        /// <summary>Percentile con interpolazione lineare tra i ranghi (array ordinato crescente).</summary>
        private static double Percentile(float[] sorted, double p)
        {
            int n = sorted.Length;
            if (n == 0) return 0;
            if (n == 1) return sorted[0];
            double rank = p * (n - 1);
            int lo = (int)Math.Floor(rank);
            int hi = Math.Min(n - 1, lo + 1);
            double frac = rank - lo;
            return sorted[lo] + (sorted[hi] - sorted[lo]) * frac;
        }

        private static double Consistency(double ratio, double stuttersPerMin)
        {
            if (double.IsNaN(ratio)) return 0;
            ratio = Math.Clamp(ratio, 0, 1);
            double ratioScore = Math.Clamp((ratio - 0.30) / 0.65, 0, 1) * 100.0;
            double penalty = 30.0 * (1.0 - Math.Exp(-Math.Max(0, stuttersPerMin) / 10.0));
            return Math.Clamp(ratioScore - penalty, 0, 100);
        }

        /// <summary>
        /// Per ogni secondo s della sessione (che finisce all'istante firstEndMs + s·1000) l'indice del campione
        /// con l'istante più vicino, oppure -1 se il più vicino dista più di maxDistMs. Gli istanti NaN sono ignorati
        /// (anche non ordinati). Serve ad allineare per tempo i campioni a ~1 Hz, che derivano rispetto ai frame.
        /// </summary>
        public static int[] NearestSamples(IReadOnlyList<double> sampleMs, double firstEndMs, int nSec,
            double maxDistMs = double.PositiveInfinity)
        {
            var result = new int[Math.Max(0, nSec)];
            Array.Fill(result, -1);
            var order = new List<int>();
            for (int i = 0; i < sampleMs.Count; i++)
                if (double.IsFinite(sampleMs[i])) order.Add(i);
            if (order.Count == 0 || !double.IsFinite(firstEndMs)) return result;
            order.Sort((a, b) => sampleMs[a].CompareTo(sampleMs[b]));

            int j = 0;
            for (int s = 0; s < result.Length; s++)
            {
                double target = firstEndMs + s * 1000.0;
                // Avanza finché il campione successivo è almeno altrettanto vicino (bersagli crescenti → monotono).
                while (j + 1 < order.Count &&
                       Math.Abs(sampleMs[order[j + 1]] - target) <= Math.Abs(sampleMs[order[j]] - target))
                    j++;
                if (Math.Abs(sampleMs[order[j]] - target) <= maxDistMs) result[s] = order[j];
            }
            return result;
        }

        /// <summary>
        /// Per ogni campione, il secondo della sessione la cui fine è più vicina al suo istante (-1 se fuori dalla
        /// sessione o NaN). A differenza di <see cref="NearestSamples"/> ogni campione finisce in un solo secondo:
        /// va usato per gli eventi da contare (es. freeze di rete), che non devono essere duplicati.
        /// </summary>
        public static int[] SecondOfSamples(IReadOnlyList<double> sampleMs, double firstEndMs, int nSec)
        {
            var result = new int[sampleMs.Count];
            for (int i = 0; i < result.Length; i++)
            {
                double t = sampleMs[i];
                result[i] = -1;
                if (!double.IsFinite(t) || !double.IsFinite(firstEndMs)) continue;
                double k = Math.Round((t - firstEndMs) / 1000.0, MidpointRounding.AwayFromZero);
                if (k >= 0 && k < nSec) result[i] = (int)k;
            }
            return result;
        }
    }
}
