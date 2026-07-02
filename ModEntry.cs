using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Mods;
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

        // ---- Multi-pass draw timing (via Game1's own hooks system) ----
        // Game1 has a built-in extension point for exactly this: `Game1.hooks` is a
        // `protected internal static ModHooks` field, and every major draw pass calls
        // hooks.OnRendering(step, ...)/OnRendered(step, ...) with a RenderSteps value
        // (HUD, World, World_Background, World_Sorted, World_AlwaysFront, World_Weather,
        // World_RenderLightmap, World_DrawLightmapOnScreen, Menu, GlobalFade, Overlays, etc)
        // — see StardewValley.Mods.RenderSteps / StardewValley.Mods.ModHooks in the
        // decompiled source. Wrapping this (instead of Harmony-patching each draw method
        // individually) gives an exact, official breakdown with far less risk of colliding
        // with other mods that patch the same private methods.
        //
        // IMPORTANT: we CHAIN to whatever hooks instance was already installed (likely
        // SMAPI's own) rather than replacing it outright, so we don't break anything else
        // relying on Game1.hooks.
        private static readonly Dictionary<RenderSteps, Stopwatch> StepStopwatches = new();
        private static readonly Dictionary<RenderSteps, double> LastStepMs = new();
        private readonly Dictionary<RenderSteps, double> _stepMsInWindow = new();

        private static readonly RenderSteps[] TrackedSteps =
        {
            RenderSteps.FullScene,
            RenderSteps.HUD,
            RenderSteps.World,
            RenderSteps.World_Background,
            RenderSteps.World_Sorted,
            RenderSteps.World_AlwaysFront,
            RenderSteps.World_Weather,
            RenderSteps.World_RenderLightmap,
            RenderSteps.World_DrawLightmapOnScreen,
            RenderSteps.Menu,
            RenderSteps.MenuBackground,
            RenderSteps.GlobalFade,
            RenderSteps.Overlays,
            RenderSteps.DialogueBox,
        };

        public override void Entry(IModHelper helper)
        {
            helper.Events.GameLoop.UpdateTicking += OnUpdateTicking;
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.Display.Rendering += OnRendering;
            helper.Events.Display.Rendered += OnRendered;

            // NOTE on measuring other mods' draw time: no extra instrumentation is needed.
            // Our draw= total (Display.Rendering -> Display.Rendered) covers EVERYTHING:
            // Game1's own rendering plus every other mod's event-based overlay drawing.
            // FullScene= covers only Game1._draw() itself. Therefore:
            //     draw - FullScene ≈ total time all other mods spend drawing overlays.
            // That difference is the number that identifies whether "other mods" are the
            // bottleneck, without touching any of their code.

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
                _stepMsInWindow.Clear();
                foreach (var step in TrackedSteps)
                    LastStepMs[step] = 0;
                Monitor.Log("LagProfiler active. Watching for full frames (Update+Draw) slower than " + SpikeThresholdMs + "ms.", LogLevel.Info);
            };

            InstallRenderStepHooks();
        }

        private void InstallRenderStepHooks()
        {
            try
            {
                foreach (var step in TrackedSteps)
                {
                    StepStopwatches[step] = new Stopwatch();
                    LastStepMs[step] = 0;
                }

                FieldInfo? hooksField = typeof(Game1).GetField("hooks",
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (hooksField == null)
                {
                    Monitor.Log("Could not find Game1.hooks field — render-step timing disabled, but frame/update/draw totals still work fine.", LogLevel.Warn);
                    return;
                }

                var existingHooks = hooksField.GetValue(null) as ModHooks;
                var timingHooks = new TimingModHooks(existingHooks);
                hooksField.SetValue(null, timingHooks);

                Monitor.Log($"Render-step timing installed (chained to {(existingHooks == null ? "no prior hooks" : existingHooks.GetType().Name)}), tracking {TrackedSteps.Length} steps.", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Monitor.Log($"Could not install render-step timing, skipping that part: {ex.Message}", LogLevel.Warn);
            }
        }

        /// <summary>
        /// Wraps the game's existing ModHooks instance to time each RenderSteps pass.
        /// Always forwards to the inner hooks and returns/preserves its result — never
        /// changes actual rendering behavior, only observes timing around it.
        /// </summary>
        private class TimingModHooks : ModHooks
        {
            private readonly ModHooks? _inner;

            public TimingModHooks(ModHooks? inner)
            {
                _inner = inner;
            }

            public override bool OnRendering(RenderSteps step, SpriteBatch sb, GameTime time, RenderTarget2D target_screen)
            {
                if (StepStopwatches.TryGetValue(step, out var sw))
                    sw.Restart();

                // Preserve whatever the previously-installed hooks would have done.
                return _inner?.OnRendering(step, sb, time, target_screen) ?? base.OnRendering(step, sb, time, target_screen);
            }

            public override void OnRendered(RenderSteps step, SpriteBatch sb, GameTime time, RenderTarget2D target_screen)
            {
                if (StepStopwatches.TryGetValue(step, out var sw))
                    LastStepMs[step] = sw.Elapsed.TotalMilliseconds;

                if (_inner != null)
                    _inner.OnRendered(step, sb, time, target_screen);
                else
                    base.OnRendered(step, sb, time, target_screen);
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

            // Snapshot this frame's per-step timings, then reset so a step that didn't run
            // this frame (e.g. no menu open) correctly shows 0 rather than a stale value.
            var stepSnapshot = new Dictionary<RenderSteps, double>(LastStepMs);
            foreach (var step in TrackedSteps)
                LastStepMs[step] = 0;

            ProcessFrameTime(frameMs, _lastUpdateMs, _lastDrawMs, stepSnapshot);
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

        private static string FormatSteps(Dictionary<RenderSteps, double> steps)
        {
            var parts = new List<string>();
            foreach (var step in TrackedSteps)
                parts.Add($"{step}={(steps.TryGetValue(step, out var ms) ? ms : 0):F1}ms");
            return string.Join(" ", parts);
        }

        private void ProcessFrameTime(double elapsedMs, double updateMs, double drawMs, Dictionary<RenderSteps, double> steps)
        {
            if (!Context.IsWorldReady)
                return;

            GameLocation loc = Game1.currentLocation;
            string locName = loc?.NameOrUniqueName ?? "unknown";

            _ticksInWindow++;
            _msInWindow += elapsedMs;
            _updateMsInWindow += updateMs;
            _drawMsInWindow += drawMs;
            foreach (var kv in steps)
                _stepMsInWindow[kv.Key] = _stepMsInWindow.GetValueOrDefault(kv.Key, 0) + kv.Value;

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
                    $"[SPIKE] {elapsedMs:F1}ms frame (update={updateMs:F1}ms draw={drawMs:F1}ms other={otherMs:F1}ms) [{FormatSteps(steps)}] | location={locName} | NPCs={counts.NpcCount} | monsters={counts.MonsterCount} | "
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

                var avgSteps = new Dictionary<RenderSteps, double>();
                foreach (var step in TrackedSteps)
                    avgSteps[step] = _ticksInWindow > 0 ? _stepMsInWindow.GetValueOrDefault(step, 0) / _ticksInWindow : 0;

                Monitor.Log(
                    $"[SUMMARY] last {SummaryIntervalSec}s: avg={avgMs:F1}ms (~{approxFps:F0} FPS) "
                    + $"[update={avgUpdateMs:F1}ms draw={avgDrawMs:F1}ms other={avgOtherMs:F1}ms] [{FormatSteps(avgSteps)}] | worstFrame={_worstMsInWindow:F1}ms at {_worstLocationInWindow} | currentLocation={locName}",
                    LogLevel.Info);

                _summaryStopwatch.Restart();
                _ticksInWindow = 0;
                _msInWindow = 0;
                _updateMsInWindow = 0;
                _drawMsInWindow = 0;
                _stepMsInWindow.Clear();
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
