using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Monsters;

namespace LagProfiler
{
    public class ModEntry : Mod
    {
        // ---- Config (edit these numbers to tune sensitivity) ----
        private const double SpikeThresholdMs = 50.0;   // ~below 20 FPS if a full frame regularly takes this long
        private const double SummaryIntervalSec = 5.0;  // how often to print the rolling summary

        private readonly Stopwatch _summaryStopwatch = new();
        private readonly Stopwatch _frameStopwatch = new();
        private readonly Stopwatch _updateStopwatch = new();
        private readonly Stopwatch _drawStopwatch = new();

        private bool _hasPreviousTick;

        private double _lastUpdateMs;
        private double _lastDrawMs;

        private int _ticksInWindow;
        private double _msInWindow;
        private double _updateMsInWindow;
        private double _drawMsInWindow;
        private double _worstMsInWindow;
        private string _worstLocationInWindow = "-";

        // ---- Multi-pass draw timing ----
        // Times each named sub-pass of Game1's draw pipeline (world, weather, lighting, etc)
        // via Harmony prefix/postfix pairs, so we can see which specific pass is actually
        // eating the "draw" time reported above, instead of just knowing "draw = 41ms" with
        // no idea where inside that 41ms it's going.
        //
        // Method names here are the exact private/public method names on Game1 as found in
        // the decompiled source — see StardewValley/Game1.cs: DrawWorld, drawWeather,
        // DrawLighting, DrawLightmapOnScreen, DrawCharacterEmotes, DrawScreenOverlaySprites,
        // DrawGlobalFade.
        private static readonly string[] PassNames =
        {
            "DrawWorld",
            "drawWeather",
            "DrawLighting",
            "DrawLightmapOnScreen",
            "DrawCharacterEmotes",
            "DrawScreenOverlaySprites",
            "DrawGlobalFade",
            "drawHUD",
            "DrawOverlays",
            "DrawMenu",
        };

        private static readonly Dictionary<string, Stopwatch> PassStopwatches = new();
        private static readonly Dictionary<string, double> LastPassMs = new();
        private readonly Dictionary<string, double> _passMsInWindow = new();

        public override void Entry(IModHelper helper)
        {
            helper.Events.GameLoop.UpdateTicking += OnUpdateTicking;
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.Display.Rendering += OnRendering;
            helper.Events.Display.Rendered += OnRendered;
            helper.Events.GameLoop.SaveLoaded += (_, _) =>
            {
                _summaryStopwatch.Restart();
                _frameStopwatch.Restart();
                _hasPreviousTick = false;
                _lastUpdateMs = 0;
                _lastDrawMs = 0;
                _ticksInWindow = 0;
                _msInWindow = 0;
                _updateMsInWindow = 0;
                _drawMsInWindow = 0;
                _worstMsInWindow = 0;
                _worstLocationInWindow = "-";
                _passMsInWindow.Clear();
                foreach (var name in PassNames)
                    LastPassMs[name] = 0;
                Monitor.Log("LagProfiler active. Watching for full frames (Update+Draw) slower than " + SpikeThresholdMs + "ms.", LogLevel.Info);
            };

            try
            {
                var harmony = new Harmony(this.ModManifest.UniqueID);
                int patchedCount = 0;

                foreach (var name in PassNames)
                {
                    try
                    {
                        var method = AccessTools.Method(typeof(Game1), name);
                        if (method == null)
                        {
                            Monitor.Log($"Could not find Game1.{name} to time — skipping that pass.", LogLevel.Warn);
                            continue;
                        }

                        PassStopwatches[name] = new Stopwatch();
                        LastPassMs[name] = 0;

                        harmony.Patch(
                            original: method,
                            prefix: new HarmonyMethod(typeof(PassTimingPatches), nameof(PassTimingPatches.Prefix)),
                            postfix: new HarmonyMethod(typeof(PassTimingPatches), nameof(PassTimingPatches.Postfix))
                        );
                        patchedCount++;
                    }
                    catch (Exception exInner)
                    {
                        Monitor.Log($"Could not time Game1.{name}, skipping just that pass: {exInner.Message}", LogLevel.Warn);
                    }
                }

                Monitor.Log($"Draw sub-pass timing attached to {patchedCount}/{PassNames.Length} passes.", LogLevel.Info);
            }
            catch (Exception ex)
            {
                // If any of this fails (game version differences, etc), the rest of
                // LagProfiler (frame/update/draw totals) still works fine without it.
                Monitor.Log($"Could not attach draw sub-pass timing, skipping that part: {ex.Message}", LogLevel.Warn);
            }
        }

        /// <summary>
        /// Shared prefix/postfix used for every patched sub-pass — looks up which pass called
        /// it via __originalMethod instead of needing one copy-pasted method pair per pass.
        /// </summary>
        internal static class PassTimingPatches
        {
            internal static void Prefix(MethodBase __originalMethod)
            {
                if (PassStopwatches.TryGetValue(__originalMethod.Name, out var sw))
                    sw.Restart();
            }

            internal static void Postfix(MethodBase __originalMethod)
            {
                if (PassStopwatches.TryGetValue(__originalMethod.Name, out var sw))
                    LastPassMs[__originalMethod.Name] = sw.Elapsed.TotalMilliseconds;
            }
        }

        private void OnUpdateTicking(object? sender, UpdateTickingEventArgs e)
        {
            _updateStopwatch.Restart();

            if (!_hasPreviousTick)
            {
                _frameStopwatch.Restart();
                _hasPreviousTick = true;
                return;
            }

            double frameMs = _frameStopwatch.Elapsed.TotalMilliseconds;
            _frameStopwatch.Restart();

            // Snapshot this frame's pass timings, then reset them so a pass that didn't run
            // this frame (e.g. no weather) correctly shows as 0 rather than a stale value.
            var passSnapshot = new Dictionary<string, double>(LastPassMs);
            foreach (var name in PassNames)
                LastPassMs[name] = 0;

            ProcessFrameTime(frameMs, _lastUpdateMs, _lastDrawMs, passSnapshot);
        }

        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            _lastUpdateMs = _updateStopwatch.Elapsed.TotalMilliseconds;
        }

        private void OnRendering(object? sender, RenderingEventArgs e)
        {
            _drawStopwatch.Restart();
        }

        private void OnRendered(object? sender, RenderedEventArgs e)
        {
            _lastDrawMs = _drawStopwatch.Elapsed.TotalMilliseconds;
        }

        private static string FormatPasses(Dictionary<string, double> passes)
        {
            var parts = new List<string>();
            foreach (var name in PassNames)
                parts.Add($"{name}={(passes.TryGetValue(name, out var ms) ? ms : 0):F1}ms");
            return string.Join(" ", parts);
        }

        private void ProcessFrameTime(double elapsedMs, double updateMs, double drawMs, Dictionary<string, double> passes)
        {
            if (!Context.IsWorldReady)
                return;

            GameLocation loc = Game1.currentLocation;
            string locName = loc?.NameOrUniqueName ?? "unknown";

            _ticksInWindow++;
            _msInWindow += elapsedMs;
            _updateMsInWindow += updateMs;
            _drawMsInWindow += drawMs;
            foreach (var kv in passes)
                _passMsInWindow[kv.Key] = _passMsInWindow.GetValueOrDefault(kv.Key, 0) + kv.Value;

            if (elapsedMs > _worstMsInWindow)
            {
                _worstMsInWindow = elapsedMs;
                _worstLocationInWindow = locName;
            }

            if (elapsedMs >= SpikeThresholdMs && loc != null)
            {
                EntityCounts counts = GetEntityCounts(loc);
                double otherMs = Math.Max(0, elapsedMs - updateMs - drawMs);

                Monitor.Log(
                    $"[SPIKE] {elapsedMs:F1}ms frame (update={updateMs:F1}ms draw={drawMs:F1}ms other={otherMs:F1}ms) [{FormatPasses(passes)}] | location={locName} | NPCs={counts.NpcCount} | monsters={counts.MonsterCount} | "
                    + $"objects={counts.ObjectCount} | sprinklers={counts.SprinklerCount} | kegs={counts.KegCount} | casks={counts.CaskCount} | "
                    + $"otherMachines={counts.OtherMachineCount} | buildings={counts.BuildingCount} | terrainFeatures={counts.TerrainFeatureCount} | "
                    + $"tempSprites={counts.TempSpriteCount} | debris={counts.DebrisCount} | playerTile={Game1.player.Tile}",
                    LogLevel.Warn);
            }

            if (_summaryStopwatch.Elapsed.TotalSeconds >= SummaryIntervalSec)
            {
                double avgMs = _ticksInWindow > 0 ? _msInWindow / _ticksInWindow : 0;
                double avgUpdateMs = _ticksInWindow > 0 ? _updateMsInWindow / _ticksInWindow : 0;
                double avgDrawMs = _ticksInWindow > 0 ? _drawMsInWindow / _ticksInWindow : 0;
                double avgOtherMs = Math.Max(0, avgMs - avgUpdateMs - avgDrawMs);
                double approxFps = avgMs > 0 ? Math.Min(60.0, 1000.0 / avgMs) : 60.0;

                var avgPasses = new Dictionary<string, double>();
                foreach (var name in PassNames)
                    avgPasses[name] = _ticksInWindow > 0 ? _passMsInWindow.GetValueOrDefault(name, 0) / _ticksInWindow : 0;

                Monitor.Log(
                    $"[SUMMARY] last {SummaryIntervalSec}s: avg={avgMs:F1}ms (~{approxFps:F0} FPS) "
                    + $"[update={avgUpdateMs:F1}ms draw={avgDrawMs:F1}ms other={avgOtherMs:F1}ms] [{FormatPasses(avgPasses)}] | worstFrame={_worstMsInWindow:F1}ms at {_worstLocationInWindow} | currentLocation={locName}",
                    LogLevel.Info);

                _summaryStopwatch.Restart();
                _ticksInWindow = 0;
                _msInWindow = 0;
                _updateMsInWindow = 0;
                _drawMsInWindow = 0;
                _passMsInWindow.Clear();
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

        private EntityCounts GetEntityCounts(GameLocation loc)
        {
            var counts = new EntityCounts();

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
                foreach (StardewValley.Object obj in loc.Objects.Values)
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

            if (loc is Farm farm)
                counts.BuildingCount = farm.buildings?.Count ?? 0;

            return counts;
        }
    }
}
