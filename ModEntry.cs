using System;
using System.Diagnostics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Monsters;

namespace LagProfiler
{
    public class ModEntry : Mod
    {
        // ---- Config (edit these numbers to tune sensitivity) ----
        private const double SpikeThresholdMs = 50.0;   // ~below 20 FPS if a tick regularly takes this long
        private const double SummaryIntervalSec = 5.0;  // how often to print the rolling summary

        private readonly Stopwatch _tickStopwatch = new();
        private readonly Stopwatch _summaryStopwatch = new();

        private int _ticksInWindow;
        private double _msInWindow;
        private double _worstMsInWindow;
        private string _worstLocationInWindow = "-";

        public override void Entry(IModHelper helper)
        {
            helper.Events.GameLoop.UpdateTicking += OnUpdateTicking;
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.GameLoop.SaveLoaded += (_, _) =>
            {
                _summaryStopwatch.Restart();
                _ticksInWindow = 0;
                _msInWindow = 0;
                _worstMsInWindow = 0;
                _worstLocationInWindow = "-";
                Monitor.Log("LagProfiler active. Watching for ticks slower than " + SpikeThresholdMs + "ms.", LogLevel.Info);
            };
        }

        private void OnUpdateTicking(object? sender, UpdateTickingEventArgs e)
        {
            _tickStopwatch.Restart();
        }

        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            _tickStopwatch.Stop();
            double elapsedMs = _tickStopwatch.Elapsed.TotalMilliseconds;

            if (!Context.IsWorldReady)
                return;

            GameLocation loc = Game1.currentLocation;
            string locName = loc?.NameOrUniqueName ?? "unknown";

            _ticksInWindow++;
            _msInWindow += elapsedMs;
            if (elapsedMs > _worstMsInWindow)
            {
                _worstMsInWindow = elapsedMs;
                _worstLocationInWindow = locName;
            }

            if (elapsedMs >= SpikeThresholdMs && loc != null)
            {
                EntityCounts counts = GetEntityCounts(loc);

                Monitor.Log(
                    $"[SPIKE] {elapsedMs:F1}ms tick | location={locName} | NPCs={counts.NpcCount} | monsters={counts.MonsterCount} | "
                    + $"objects={counts.ObjectCount} | sprinklers={counts.SprinklerCount} | kegs={counts.KegCount} | casks={counts.CaskCount} | "
                    + $"otherMachines={counts.OtherMachineCount} | buildings={counts.BuildingCount} | terrainFeatures={counts.TerrainFeatureCount} | "
                    + $"tempSprites={counts.TempSpriteCount} | debris={counts.DebrisCount} | playerTile={Game1.player.Tile}",
                    LogLevel.Warn);
            }

            if (_summaryStopwatch.Elapsed.TotalSeconds >= SummaryIntervalSec)
            {
                double avgMs = _ticksInWindow > 0 ? _msInWindow / _ticksInWindow : 0;
                double approxFps = avgMs > 0 ? Math.Min(60.0, 1000.0 / avgMs) : 60.0;

                Monitor.Log(
                    $"[SUMMARY] last {SummaryIntervalSec}s: avg={avgMs:F1}ms (~{approxFps:F0} FPS) | worstTick={_worstMsInWindow:F1}ms at {_worstLocationInWindow} | currentLocation={locName}",
                    LogLevel.Info);

                _summaryStopwatch.Restart();
                _ticksInWindow = 0;
                _msInWindow = 0;
                _worstMsInWindow = 0;
                _worstLocationInWindow = "-";
            }
        }

        private struct EntityCounts
        {
            public int NpcCount;
            public int MonsterCount;
            public int ObjectCount;
            public int SprinklerCount;
            public int KegCount;
            public int CaskCount;
            public int OtherMachineCount;
            public int BuildingCount;
            public int TerrainFeatureCount;
            public int TempSpriteCount;
            public int DebrisCount;
        }

        /// <summary>
        /// Counts NPCs/monsters/machines/etc in the given location. Defensive on purpose
        /// (null checks everywhere) since this runs on every lag spike and must never itself
        /// add extra lag or throw.
        /// </summary>
        private EntityCounts GetEntityCounts(GameLocation loc)
        {
            var counts = new EntityCounts();

            // characters holds both NPCs and monsters (Monster : NPC in 1.6), so split by type.
            if (loc.characters != null)
            {
                foreach (NPC npc in loc.characters)
                {
                    if (npc is Monster)
                        counts.MonsterCount++;
                    else
                        counts.NpcCount++;
                }
            }

            if (loc.Objects != null)
            {
                foreach (Object obj in loc.Objects.Values)
                {
                    if (obj == null)
                        continue;

                    counts.ObjectCount++;

                    string name = obj.Name ?? string.Empty;

                    if (obj.IsSprinkler())
                        counts.SprinklerCount++;
                    else if (name.IndexOf("Keg", StringComparison.OrdinalIgnoreCase) >= 0)
                        counts.KegCount++;
                    else if (name.IndexOf("Cask", StringComparison.OrdinalIgnoreCase) >= 0)
                        counts.CaskCount++;
                    else if (obj.bigCraftable.Value)
                        counts.OtherMachineCount++;
                }
            }

            counts.TerrainFeatureCount = loc.terrainFeatures?.Length ?? 0;
            counts.TempSpriteCount = loc.TemporarySprites?.Count ?? 0;
            counts.DebrisCount = loc.debris?.Count ?? 0;

            // Buildings only exist on farm-type / buildable locations.
            if (loc is BuildableGameLocation buildable)
                counts.BuildingCount = buildable.buildings?.Count ?? 0;

            return counts;
        }
    }
}
