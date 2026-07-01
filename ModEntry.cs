using System;
using System.Diagnostics;
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

        // Diagnostic toggle: the lighting timing patches (Harmony prefix/postfix on
        // Game1.DrawLighting / Game1.DrawLightmapOnScreen) are the most invasive part of
        // this mod — they patch hot-path engine methods rather than just listening to SMAPI
        // events. If lag/RAM issues correlate with this mod being installed, disable this
        // first to isolate whether the lighting patches are the cause.
        private const bool EnableLightingTimingPatches = false;

        private readonly Stopwatch _summaryStopwatch = new();
        private readonly Stopwatch _frameStopwatch = new();
        private readonly Stopwatch _updateStopwatch = new();
        private readonly Stopwatch _drawStopwatch = new();

        private bool _hasPreviousTick;

        // Update/Draw durations measured during the span that just completed, captured at
        // the start of the next tick (see OnUpdateTicking for why this ordering works).
        private double _lastUpdateMs;
        private double _lastDrawMs;

        private int _ticksInWindow;
        private double _msInWindow;
        private double _updateMsInWindow;
        private double _drawMsInWindow;
        private double _worstMsInWindow;
        private string _worstLocationInWindow = "-";

        // RAM/GC tracking. GC.CollectionCount deltas tell us how often the garbage collector
        // ran during the window — GC pauses are a common cause of stutter on memory-constrained
        // Android devices that doesn't show up as "update" or "draw" time in the split above.
        private int _gen0AtWindowStart;
        private int _gen1AtWindowStart;
        private int _gen2AtWindowStart;

        // Environment.WorkingSet may not be supported on every runtime (e.g. some Mono/Android
        // builds) — probe once and remember the result instead of hitting the exception path
        // every single window.
        private bool _workingSetChecked;
        private bool _workingSetSupported;

        // Lighting pass timing (subset of "draw"). DrawLighting renders every light source
        // into the lightmap render target; DrawLightmapOnScreen blits that texture over the
        // screen. These are timed via Harmony patches (see LightingTimingPatches below)
        // since SMAPI has no built-in event for them.
        private static readonly Stopwatch DrawLightingStopwatch = new();
        private static readonly Stopwatch DrawLightmapOnScreenStopwatch = new();
        private static double _lastDrawLightingMs;
        private static double _lastDrawLightmapOnScreenMs;
        private double _lightingMsInWindow;
        private double _lightmapBlitMsInWindow;

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
                _lightingMsInWindow = 0;
                _lightmapBlitMsInWindow = 0;
                _worstMsInWindow = 0;
                _worstLocationInWindow = "-";
                _gen0AtWindowStart = GC.CollectionCount(0);
                _gen1AtWindowStart = GC.CollectionCount(1);
                _gen2AtWindowStart = GC.CollectionCount(2);
                Monitor.Log("LagProfiler active. Watching for full frames (Update+Draw) slower than " + SpikeThresholdMs + "ms.", LogLevel.Info);
            };

            if (!EnableLightingTimingPatches)
            {
                Monitor.Log("Lighting pass timing patches are disabled (diagnostic mode) — lighting=0.0ms in logs is expected right now.", LogLevel.Info);
                return;
            }

            try
            {
                var harmony = new Harmony(this.ModManifest.UniqueID);

                harmony.Patch(
                    original: AccessTools.Method(typeof(Game1), "DrawLighting"),
                    prefix: new HarmonyMethod(typeof(LightingTimingPatches), nameof(LightingTimingPatches.DrawLighting_Prefix)),
                    postfix: new HarmonyMethod(typeof(LightingTimingPatches), nameof(LightingTimingPatches.DrawLighting_Postfix))
                );
                harmony.Patch(
                    original: AccessTools.Method(typeof(Game1), "DrawLightmapOnScreen"),
                    prefix: new HarmonyMethod(typeof(LightingTimingPatches), nameof(LightingTimingPatches.DrawLightmapOnScreen_Prefix)),
                    postfix: new HarmonyMethod(typeof(LightingTimingPatches), nameof(LightingTimingPatches.DrawLightmapOnScreen_Postfix))
                );

                Monitor.Log("Lighting pass timing patches applied (DrawLighting, DrawLightmapOnScreen).", LogLevel.Info);
            }
            catch (Exception ex)
            {
                // If the game's internal method names/signatures ever change, fail open:
                // the rest of LagProfiler (update/draw/RAM) still works fine without this.
                Monitor.Log($"Could not attach lighting timing patches, skipping that part: {ex.Message}", LogLevel.Warn);
            }
        }

        /// <summary>Static so Harmony patches (which must be static methods) can reach it.</summary>
        internal static class LightingTimingPatches
        {
            internal static void DrawLighting_Prefix() => DrawLightingStopwatch.Restart();

            internal static void DrawLighting_Postfix()
            {
                _lastDrawLightingMs = DrawLightingStopwatch.Elapsed.TotalMilliseconds;
            }

            internal static void DrawLightmapOnScreen_Prefix() => DrawLightmapOnScreenStopwatch.Restart();

            internal static void DrawLightmapOnScreen_Postfix()
            {
                _lastDrawLightmapOnScreenMs = DrawLightmapOnScreenStopwatch.Elapsed.TotalMilliseconds;
            }
        }

        private void OnUpdateTicking(object? sender, UpdateTickingEventArgs e)
        {
            // Game loop order per iteration: UpdateTicking -> ... -> UpdateTicked -> ... ->
            // Rendering -> ... -> Rendered -> (loop) -> next UpdateTicking. So by the time
            // this fires again, _lastUpdateMs/_lastDrawMs hold the Update and Draw durations
            // for the span that just finished — measuring the full Update+Draw loop covers
            // what an on-screen FPS counter actually sees, split into its two halves.
            _updateStopwatch.Restart();

            if (!_hasPreviousTick)
            {
                _frameStopwatch.Restart();
                _hasPreviousTick = true;
                return;
            }

            double frameMs = _frameStopwatch.Elapsed.TotalMilliseconds;
            _frameStopwatch.Restart();

            ProcessFrameTime(frameMs, _lastUpdateMs, _lastDrawMs, _lastDrawLightingMs, _lastDrawLightmapOnScreenMs);
            _lastDrawLightingMs = 0;
            _lastDrawLightmapOnScreenMs = 0;
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

        private void ProcessFrameTime(double elapsedMs, double updateMs, double drawMs, double lightingMs, double lightmapBlitMs)
        {
            if (!Context.IsWorldReady)
                return;

            GameLocation loc = Game1.currentLocation;
            string locName = loc?.NameOrUniqueName ?? "unknown";

            _ticksInWindow++;
            _msInWindow += elapsedMs;
            _updateMsInWindow += updateMs;
            _drawMsInWindow += drawMs;
            _lightingMsInWindow += lightingMs;
            _lightmapBlitMsInWindow += lightmapBlitMs;
            if (elapsedMs > _worstMsInWindow)
            {
                _worstMsInWindow = elapsedMs;
                _worstLocationInWindow = locName;
            }

            if (elapsedMs >= SpikeThresholdMs && loc != null)
            {
                EntityCounts counts = GetEntityCounts(loc);
                double lightingTotalMs = lightingMs + lightmapBlitMs;
                double otherMs = Math.Max(0, elapsedMs - updateMs - drawMs);
                double heapMbNow = GC.GetTotalMemory(false) / 1024.0 / 1024.0;

                Monitor.Log(
                    $"[SPIKE] {elapsedMs:F1}ms frame (update={updateMs:F1}ms draw={drawMs:F1}ms [lighting={lightingTotalMs:F1}ms = render {lightingMs:F1}ms + blit {lightmapBlitMs:F1}ms] other={otherMs:F1}ms) | location={locName} | NPCs={counts.NpcCount} | monsters={counts.MonsterCount} | "
                    + $"objects={counts.ObjectCount} | sprinklers={counts.SprinklerCount} | kegs={counts.KegCount} | casks={counts.CaskCount} | "
                    + $"otherMachines={counts.OtherMachineCount} | buildings={counts.BuildingCount} | terrainFeatures={counts.TerrainFeatureCount} | "
                    + $"tempSprites={counts.TempSpriteCount} | debris={counts.DebrisCount} | managedHeap={heapMbNow:F1}MB | playerTile={Game1.player.Tile}",
                    LogLevel.Warn);
            }

            if (_summaryStopwatch.Elapsed.TotalSeconds >= SummaryIntervalSec)
            {
                double avgMs = _ticksInWindow > 0 ? _msInWindow / _ticksInWindow : 0;
                double avgUpdateMs = _ticksInWindow > 0 ? _updateMsInWindow / _ticksInWindow : 0;
                double avgDrawMs = _ticksInWindow > 0 ? _drawMsInWindow / _ticksInWindow : 0;
                double avgLightingMs = _ticksInWindow > 0 ? _lightingMsInWindow / _ticksInWindow : 0;
                double avgLightmapBlitMs = _ticksInWindow > 0 ? _lightmapBlitMsInWindow / _ticksInWindow : 0;
                double avgLightingTotalMs = avgLightingMs + avgLightmapBlitMs;
                double avgOtherMs = Math.Max(0, avgMs - avgUpdateMs - avgDrawMs);
                double approxFps = avgMs > 0 ? Math.Min(60.0, 1000.0 / avgMs) : 60.0;

                double heapMb = GC.GetTotalMemory(false) / 1024.0 / 1024.0;
                int gen0Collections = GC.CollectionCount(0) - _gen0AtWindowStart;
                int gen1Collections = GC.CollectionCount(1) - _gen1AtWindowStart;
                int gen2Collections = GC.CollectionCount(2) - _gen2AtWindowStart;
                string workingSetPart = TryGetWorkingSetMb(out double workingSetMb)
                    ? $" | workingSet={workingSetMb:F0}MB"
                    : "";

                Monitor.Log(
                    $"[SUMMARY] last {SummaryIntervalSec}s: avg={avgMs:F1}ms (~{approxFps:F0} FPS) "
                    + $"[update={avgUpdateMs:F1}ms draw={avgDrawMs:F1}ms (lighting={avgLightingTotalMs:F1}ms = render {avgLightingMs:F1}ms + blit {avgLightmapBlitMs:F1}ms) other={avgOtherMs:F1}ms] | worstFrame={_worstMsInWindow:F1}ms at {_worstLocationInWindow} | currentLocation={locName} "
                    + $"| managedHeap={heapMb:F1}MB{workingSetPart} | gcGen0={gen0Collections} gen1={gen1Collections} gen2={gen2Collections}",
                    LogLevel.Info);

                _summaryStopwatch.Restart();
                _ticksInWindow = 0;
                _msInWindow = 0;
                _updateMsInWindow = 0;
                _drawMsInWindow = 0;
                _lightingMsInWindow = 0;
                _lightmapBlitMsInWindow = 0;
                _worstMsInWindow = 0;
                _worstLocationInWindow = "-";
                _gen0AtWindowStart = GC.CollectionCount(0);
                _gen1AtWindowStart = GC.CollectionCount(1);
                _gen2AtWindowStart = GC.CollectionCount(2);
            }
        }

        /// <summary>
        /// Tries to read the process's physical working set in MB. Not all runtimes support
        /// Environment.WorkingSet (some Mono/Android builds throw PlatformNotSupportedException),
        /// so this probes once and remembers the result instead of retrying every window.
        /// </summary>
        private bool TryGetWorkingSetMb(out double workingSetMb)
        {
            workingSetMb = 0;

            if (_workingSetChecked && !_workingSetSupported)
                return false;

            try
            {
                workingSetMb = Environment.WorkingSet / 1024.0 / 1024.0;
                _workingSetSupported = true;
                return true;
            }
            catch
            {
                _workingSetSupported = false;
                return false;
            }
            finally
            {
                _workingSetChecked = true;
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

            // Buildings only exist on farm-type locations. BuildableGameLocation was folded
            // into GameLocation/Farm in 1.6, so check Farm specifically instead.
            if (loc is Farm farm)
                counts.BuildingCount = farm.buildings?.Count ?? 0;

            return counts;
        }
    }
}

