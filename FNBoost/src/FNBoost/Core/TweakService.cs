using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace FNBoost.Core
{
    /// <summary>Applica/ripristina i tweak in background, con punto di ripristino automatico.</summary>
    public sealed class TweakService
    {
        private readonly BackupStore _store;
        private readonly AppSettings _settings;
        private bool _restorePointAttempted;

        public ObservableCollection<Tweak> Tweaks { get; }
        public bool RebootPending { get; private set; }

        public event Action? Changed;

        public TweakService(BackupStore store, AppSettings settings)
        {
            _store = store;
            _settings = settings;
            Tweaks = new ObservableCollection<Tweak>(TweakCatalog.Create());
        }

        public int RecommendedCount => Tweaks.Count(t => t.Recommended && t.IsApplicable);
        public int RecommendedActive => Tweaks.Count(t => t.Recommended && t.IsApplicable && t.IsOn);
        public int ActiveCount => Tweaks.Count(t => t.IsOn);

        public async Task RefreshAsync()
        {
            var results = await Task.Run(() => Tweaks.Select(t => (t, state: t.Detect())).ToList());
            foreach (var (t, state) in results) t.State = state;
            Changed?.Invoke();
        }

        private async Task EnsureRestorePointAsync()
        {
            if (!_settings.AutoRestorePoint || _restorePointAttempted) return;
            _restorePointAttempted = true;
            Log.Info("Creo un punto di ripristino di Windows prima delle modifiche…");
            var (ok, msg) = await Task.Run(() => RestorePoint.Create("FN Boost - prima delle ottimizzazioni"));
            if (!ok) Log.Warn(msg + " (le modifiche hanno comunque il loro backup interno)");
        }

        public async Task<bool> SetAsync(Tweak t, bool on)
        {
            if (t.IsBusy) return false;
            t.IsBusy = true;
            try
            {
                if (on) await EnsureRestorePointAsync();
                await Task.Run(() =>
                {
                    if (on) t.Apply(_store);
                    else t.Revert(_store);
                });
                if (t.RequiresReboot) RebootPending = true;
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"'{t.Title}' non riuscito", ex);
                return false;
            }
            finally
            {
                t.State = await Task.Run(t.Detect);
                t.IsBusy = false;
                t.NotifyState();
                Changed?.Invoke();
            }
        }

        public async Task<(int ok, int fail)> ApplyRecommendedAsync()
        {
            int ok = 0, fail = 0;
            await RefreshAsync();
            foreach (var t in Tweaks.Where(t => t.Recommended && t.IsApplicable && !t.IsOn).ToList())
            {
                if (await SetAsync(t, true)) ok++;
                else fail++;
            }
            Log.Info($"Profilo consigliato: {ok} applicati, {fail} errori.");
            return (ok, fail);
        }

        /// <summary>Ripristina SOLO ciò che FN Boost ha modificato (i tweak con un backup salvato).</summary>
        public async Task<(int ok, int fail)> RevertAllAsync()
        {
            int ok = 0, fail = 0;
            var ids = _store.All().Select(b => b.TweakId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var t in Tweaks.Where(t => ids.Contains(t.Id)).ToList())
            {
                if (await SetAsync(t, false)) ok++;
                else fail++;
            }
            Log.Info($"Ripristino completato: {ok} tweak ripristinati, {fail} errori.");
            return (ok, fail);
        }
    }
}
