using System;
using System.Collections.Generic;
using System.Linq;

namespace FNBoost.Tests
{
    /// <summary>Mini harness di asserzioni, senza pacchetti esterni.</summary>
    internal static class T
    {
        private static readonly List<string> Failures = new();
        private static int _passed;
        private static string _current = "";

        public static int Passed => _passed;
        public static IReadOnlyList<string> FailureList => Failures;

        public static void Run(string name, Action test)
        {
            _current = name;
            int before = Failures.Count;
            try
            {
                test();
            }
            catch (Exception ex)
            {
                Failures.Add($"{name}: eccezione {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
            if (Failures.Count == before)
            {
                _passed++;
                Console.WriteLine($"  OK   {name}");
            }
            else
            {
                Console.WriteLine($"  FAIL {name}");
                foreach (var f in Failures.Skip(before)) Console.WriteLine("       " + f);
            }
        }

        public static void True(bool condition, string what)
        {
            if (!condition) Failures.Add($"{_current}: atteso vero → {what}");
        }

        public static void Equal<TV>(TV expected, TV actual, string what)
        {
            if (!EqualityComparer<TV>.Default.Equals(expected, actual))
                Failures.Add($"{_current}: {what}: atteso {expected}, ottenuto {actual}");
        }

        public static void Near(double expected, double actual, double tolerance, string what)
        {
            if (double.IsNaN(actual) || Math.Abs(expected - actual) > tolerance)
                Failures.Add($"{_current}: {what}: atteso {expected} ± {tolerance}, ottenuto {actual}");
        }

        public static void Contains(string haystack, string needle, string what)
        {
            if (haystack == null || !haystack.Contains(needle, StringComparison.Ordinal))
                Failures.Add($"{_current}: {what}: \"{needle}\" non trovato in \"{haystack}\"");
        }
    }
}
