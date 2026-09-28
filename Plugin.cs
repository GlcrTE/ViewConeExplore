using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ViewConeExplore
{
    /// <summary>
    /// Reveals the minimap only inside the camera's view cone, up to the current visibility
    /// distance (fog / night), and only where the terrain is in line of sight.
    /// </summary>
    [BepInPlugin(Guid, ModName, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "valheim.viewconeexplore";
        public const string ModName = "View Cone Explore";
        public const string Version = "1.2.0";

        private ConfigEntry<bool> _enabled;
        private ConfigEntry<float> _maxDistance;
        private ConfigEntry<float> _minDistance;
        private ConfigEntry<float> _heightBonusPerMeter;
        private ConfigEntry<float> _heightBonusMax;
        private ConfigEntry<bool> _useFog;
        private ConfigEntry<float> _fogMultiplier;
        private ConfigEntry<float> _nightMultiplier;
        private ConfigEntry<float> _fov;
        private ConfigEntry<float> _nearRadius;
        private ConfigEntry<bool> _lineOfSight;
        private ConfigEntry<float> _interval;
        private ConfigEntry<float> _frameBudget;
        private ConfigEntry<bool> _forestEnabled;
        private ConfigEntry<float> _forestSightDistance;
        private ConfigEntry<float> _canopyHeight;
        private ConfigEntry<float> _forestDensityMultiplier;
        private ConfigEntry<bool> _fillEnabled;
        private ConfigEntry<float> _fillRadius;
        private ConfigEntry<int> _fillMaxHoleSize;
        private ConfigEntry<bool> _horizonEnabled;
        private ConfigEntry<float> _horizonMaxDistance;
        private ConfigEntry<float> _horizonFogMultiplier;
        private ConfigEntry<float> _horizonMinAngle;
        private ConfigEntry<float> _horizonInlandMinAngle;
        private ConfigEntry<float> _horizonCoastDistance;
        private ConfigEntry<KeyboardShortcut> _calibrationKey;
        private ConfigEntry<KeyboardShortcut> _horizonToggleKey;
        private float _horizonConfirmUntil = -1f;
        private const float HorizonConfirmSeconds = 3f;

        private Func<Minimap, int, int, bool> _explorePixel;
        private AccessTools.FieldRef<Minimap, Texture2D> _fogTexture;
        private AccessTools.FieldRef<Minimap, BitArray> _explored;

        // Scratch buffers for hole filling, reused between updates.
        private bool[] _fillVisited = new bool[0];
        private readonly List<int> _fillComponent = new List<int>();
        private readonly Stack<int> _fillStack = new Stack<int>();

        private struct Cell
        {
            public float Height;  // raw terrain height
            public float Ground;  // terrain height, raised to water level over water
            public float Forest;  // 0 = open, 1 = dense forest, -1 = not computed yet
        }

        // World heights and forests are procedural and never change, so caching them is safe.
        private readonly Dictionary<long, Cell> _cellCache = new Dictionary<long, Cell>();
        private const int MaxCacheEntries = 600000;
        // Same threshold the game uses for WorldGenerator.InForest().
        private const float ForestThreshold = 1.15f;
        private float _timer;
        private Sweep _sweep;
        private float _lastApply;
        private const float ProgressApplyInterval = 0.5f;
        private readonly System.Diagnostics.Stopwatch _budgetTimer = new System.Diagnostics.Stopwatch();

        private void Awake()
        {
            _enabled = Config.Bind("General", "Enabled", true, "Enable view-cone map exploration.");
            _maxDistance = Config.Bind("Distance", "MaxDistance", 0f,
                new ConfigDescription("Maximum reveal distance in meters (clear weather, daytime). 0 = follow the game's simulation distance setting (224 m on Low up to 544 m on Extreme).", new AcceptableValueRange<float>(0f, 2000f)));
            _heightBonusPerMeter = Config.Bind("Distance", "HeightBonusPerMeter", 1f,
                new ConfigDescription("Percent added to the maximum view distances (normal and horizon) per meter your eye is above the sea. Fog still limits the view. 0 = no height bonus.", new AcceptableValueRange<float>(0f, 10f)));
            _heightBonusMax = Config.Bind("Distance", "HeightBonusMax", 100f,
                new ConfigDescription("Largest height bonus in percent.", new AcceptableValueRange<float>(0f, 500f)));
            _minDistance = Config.Bind("Distance", "MinDistance", 80f,
                new ConfigDescription("Reveal distance never drops below this (thick fog, night).", new AcceptableValueRange<float>(0f, 500f)));
            _useFog = Config.Bind("Distance", "UseFogVisibility", true,
                "Derive the view distance from the current fog density (rain, mist, storms shorten it).");
            // 2.2 matches where land with trees is just barely visible through the fog (measured in DeepForest Mist).
            _fogMultiplier = Config.Bind("Distance", "FogMultiplier", 2.2f,
                new ConfigDescription("Scales the fog-derived view distance.", new AcceptableValueRange<float>(0.1f, 5f)));
            _nightMultiplier = Config.Bind("Distance", "NightMultiplier", 0.5f,
                new ConfigDescription("View distance multiplier at night (1 = no change).", new AcceptableValueRange<float>(0f, 1f)));
            _fov = Config.Bind("Cone", "FieldOfView", 0f,
                new ConfigDescription("Horizontal cone angle in degrees. 0 = use the camera's actual horizontal FOV.", new AcceptableValueRange<float>(0f, 360f)));
            _lineOfSight = Config.Bind("Cone", "LineOfSight", true,
                "Hills and mountains block the view: terrain behind them stays hidden.");
            _nearRadius = Config.Bind("Vanilla", "NearRadius", 15f,
                new ConfigDescription("Vanilla all-around explore radius (vanilla default is 100). Keeps your immediate surroundings revealed.", new AcceptableValueRange<float>(0f, 200f)));
            _interval = Config.Bind("Performance", "UpdateInterval", 0.5f,
                new ConfigDescription("Seconds between view-cone updates.", new AcceptableValueRange<float>(0.05f, 5f)));
            _frameBudget = Config.Bind("Performance", "FrameBudgetMs", 2f,
                new ConfigDescription("Milliseconds per frame spent on the view cone. An update is spread over as many frames as it needs, so a long view distance never causes a stutter. Higher values finish updates sooner.", new AcceptableValueRange<float>(0.2f, 20f)));
            _forestEnabled = Config.Bind("Forest", "ForestBlocksView", true,
                "Forests block the view when you are at tree level. Looking down on a forest from above still reveals it.");
            _forestSightDistance = Config.Bind("Forest", "ForestSightDistance", 50f,
                new ConfigDescription("How many meters you can see through dense forest.", new AcceptableValueRange<float>(5f, 500f)));
            _canopyHeight = Config.Bind("Forest", "CanopyHeight", 20f,
                new ConfigDescription("Tree height in meters. Forest only blocks the view if you are less than this above its ground, and terrain rising above the canopy stays visible.", new AcceptableValueRange<float>(1f, 60f)));
            _forestDensityMultiplier = Config.Bind("Forest", "DensityMultiplier", 1f,
                new ConfigDescription("Scales how dense forests are treated (0 = no forests).", new AcceptableValueRange<float>(0f, 3f)));
            _forestDensityMultiplier.SettingChanged += (_, __) => _cellCache.Clear();
            _fillEnabled = Config.Bind("GapFill", "FillSmallGaps", true,
                "Reveal small unexplored gaps near you that are completely surrounded by explored map, to avoid a patchwork look.");
            _fillRadius = Config.Bind("GapFill", "Radius", 100f,
                new ConfigDescription("Radius in meters around you in which gaps are filled.", new AcceptableValueRange<float>(12f, 300f)));
            _fillMaxHoleSize = Config.Bind("GapFill", "MaxGapSize", 12,
                new ConfigDescription("Largest gap that gets filled, in map pixels (1 pixel = 12 x 12 m).", new AcceptableValueRange<int>(1, 200)));
            _horizonEnabled = Config.Bind("Horizon", "Enabled", false,
                "Reveal distant coastlines and peaks that are just barely visible beyond the normal view distance. At sea and on the coast low shores count too, inland only tall peaks. Can be switched in game with ToggleKey.");
            _horizonToggleKey = Config.Bind("Horizon", "ToggleKey", new KeyboardShortcut(KeyCode.F7),
                "Switches horizon mode. Turning it on needs a second press within 3 seconds to confirm, turning it off takes effect at once.");
            _horizonMaxDistance = Config.Bind("Horizon", "MaxDistance", 1500f,
                new ConfigDescription("Maximum distance in meters at which coasts and peaks are revealed (clear weather, daytime).", new AcceptableValueRange<float>(100f, 3000f)));
            // Silhouettes against the sky stay visible through much more fog than terrain details.
            _horizonFogMultiplier = Config.Bind("Horizon", "FogMultiplier", 3.5f,
                new ConfigDescription("Scales the fog-derived distance for coasts and peaks.", new AcceptableValueRange<float>(0.1f, 10f)));
            _horizonMinAngle = Config.Bind("Horizon", "MinAngle", 0.25f,
                new ConfigDescription("At sea or on the coast: how far land must rise above the sea, as seen from you, to be noticed (degrees). 0.25 means about 4.4 m at 1000 m distance; higher values reveal only taller coasts and peaks.", new AcceptableValueRange<float>(0.01f, 5f)));
            _horizonInlandMinAngle = Config.Bind("Horizon", "InlandMinAngle", 1.5f,
                new ConfigDescription("Away from the sea: how far a peak must rise above your eye level to be noticed (degrees). 1.5 means about 26 m at 1000 m distance. 0 = only at sea and on the coast.", new AcceptableValueRange<float>(0f, 10f)));
            _horizonCoastDistance = Config.Bind("Horizon", "CoastDistance", 60f,
                new ConfigDescription("MinAngle applies while you are at sea or open ocean is within this many meters, InlandMinAngle everywhere else.", new AcceptableValueRange<float>(0f, 300f)));
            _calibrationKey = Config.Bind("Debug", "CalibrationKey", new KeyboardShortcut(KeyCode.F8),
                "Aim the crosshair at terrain you can just barely see and press this key. Distance, height, fog and the computed view distances are shown and written to the BepInEx log.");

            var exploreMethod = AccessTools.Method(typeof(Minimap), "Explore", new[] { typeof(int), typeof(int) });
            if (exploreMethod == null || exploreMethod.ReturnType != typeof(bool))
            {
                Logger.LogError("Minimap.Explore(int, int) not found - game version incompatible, mod disabled.");
                return;
            }
            _explorePixel = AccessTools.MethodDelegate<Func<Minimap, int, int, bool>>(exploreMethod);
            _fogTexture = AccessTools.FieldRefAccess<Minimap, Texture2D>("m_fogTexture");
            if (AccessTools.Field(typeof(Minimap), "m_explored")?.FieldType == typeof(BitArray))
                _explored = AccessTools.FieldRefAccess<Minimap, BitArray>("m_explored");
            else
                Logger.LogWarning("Minimap.m_explored not found or changed type - gap filling disabled.");
            Logger.LogInfo($"{ModName} {Version} loaded.");
        }

        private void Update()
        {
            if (!_enabled.Value || _explorePixel == null)
                return;

            Minimap map = Minimap.instance;
            Player player = Player.m_localPlayer;
            if (map == null || player == null || WorldGenerator.instance == null)
                return;

            map.m_exploreRadius = _nearRadius.Value;

            if (!IsTyping())
            {
                if (_horizonToggleKey.Value.IsDown())
                    ToggleHorizon(player);
                if (_calibrationKey.Value.IsDown())
                    Calibrate(player);
            }

            if (player.IsDead() || player.InInterior())
            {
                _sweep = null;
                return;
            }

            _timer += Time.deltaTime;
            if (_sweep == null || _sweep.Map != map)
            {
                if (_timer < _interval.Value)
                    return;
                Camera cam = Utils.GetMainCamera();
                if (cam == null)
                    return;
                _timer = 0f;
                _sweep = StartSweep(map, player, cam);
                if (_sweep == null)
                    return;
            }

            // Spread the rays over several frames so a long view distance never stalls a single frame.
            // All rays first cover the normal range, then the far band, so nearby terrain shows up quickly.
            _budgetTimer.Restart();
            float budgetMs = _frameBudget.Value;
            while (true)
            {
                if (_sweep.NextRay >= _sweep.RayCount)
                {
                    if (_sweep.FarPass || _sweep.FarRange <= _sweep.Range)
                        break;
                    _sweep.FarPass = true;
                    _sweep.NextRay = 0;
                }
                TraceRay(_sweep, _sweep.NextRay++);
                if (_budgetTimer.Elapsed.TotalMilliseconds >= budgetMs)
                {
                    // Long sweeps show their progress on the map instead of only at the end.
                    if (_sweep.Changed && Time.time - _lastApply >= ProgressApplyInterval)
                        ApplyFog(_sweep);
                    return;
                }
            }

            if (_fillEnabled.Value && _explored != null && FillSmallGaps(map, player.transform.position))
                _sweep.Changed = true;
            if (_sweep.Changed)
                ApplyFog(_sweep);
            _sweep = null;
        }

        /// <summary>
        /// Horizon mode reveals large areas at once, so switching it on needs a confirming second press.
        /// </summary>
        private void ToggleHorizon(Player player)
        {
            string state;
            if (_horizonEnabled.Value)
            {
                _horizonEnabled.Value = false;
                state = "Horizon mode off";
            }
            else if (Time.time <= _horizonConfirmUntil)
            {
                _horizonEnabled.Value = true;
                _horizonConfirmUntil = -1f;
                state = "Horizon mode on";
            }
            else
            {
                _horizonConfirmUntil = Time.time + HorizonConfirmSeconds;
                player.Message(MessageHud.MessageType.TopLeft,
                    $"Press {_horizonToggleKey.Value} again to turn on horizon mode (reveals distant coasts and peaks)");
                return;
            }

            player.Message(MessageHud.MessageType.TopLeft, state);
            // Start a fresh update with the new setting right away.
            _sweep = null;
            _timer = _interval.Value;
        }

        private static bool IsTyping()
        {
            return (Chat.instance != null && Chat.instance.HasFocus()) || global::Console.IsVisible()
                   || TextInput.IsVisible() || Minimap.InTextInput() || Menu.IsVisible();
        }

        private void ApplyFog(Sweep s)
        {
            _fogTexture(s.Map).Apply();
            s.Changed = false;
            _lastApply = Time.time;
        }

        /// <summary>One pass over the view cone, captured when it starts and traced over several frames.</summary>
        private sealed class Sweep
        {
            public Minimap Map;
            public Vector3 Eye;
            public float BaseYaw, FovRad, Range, FarRange, Step, FarStep, WaterLevel;
            public int RayCount, NextRay, TextureSize, Half;
            public float PixelSize;
            public bool LineOfSight, Forest, Horizon, Changed, FarPass;
            public float ForestSight, Canopy, RiseBase, MinRiseSlope;
        }

        private Sweep StartSweep(Minimap map, Player player, Camera cam)
        {
            Vector3 eye = player.m_eye != null ? player.m_eye.position : player.transform.position + Vector3.up * 1.7f;
            float waterLevel = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
            float heightBonus = GetHeightBonus(eye.y, waterLevel);

            float range = GetViewDistance(GetMaxDistance() * heightBonus, _minDistance.Value, _fogMultiplier.Value);
            // Horizon mode: beyond the normal range, only land that stands out against the sky is revealed.
            bool coastal = IsNearOcean(player.transform.position);
            float horizonRange = _horizonEnabled.Value && (coastal || _horizonInlandMinAngle.Value > 0f)
                ? GetViewDistance(_horizonMaxDistance.Value * heightBonus, 0f, _horizonFogMultiplier.Value)
                : 0f;
            float farRange = Mathf.Max(range, horizonRange);
            if (farRange <= 0f)
                return null;

            Vector3 forward = cam.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
            {
                // Looking straight up/down: use the camera's up vector as heading.
                forward = cam.transform.up;
                forward.y = 0f;
            }
            forward.Normalize();

            float fovDeg = _fov.Value > 0f
                ? _fov.Value
                : 2f * Mathf.Atan(Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * cam.aspect) * Mathf.Rad2Deg;
            float fovRad = Mathf.Clamp(fovDeg, 1f, 360f) * Mathf.Deg2Rad;

            float pixelSize = map.m_pixelSize;

            if (_cellCache.Count > MaxCacheEntries)
                _cellCache.Clear();

            return new Sweep
            {
                Map = map,
                Eye = eye,
                BaseYaw = Mathf.Atan2(forward.x, forward.z),
                FovRad = fovRad,
                Range = range,
                FarRange = farRange,
                Step = pixelSize * 0.5f,
                // Beyond the normal range only large silhouettes count, so one sample per map pixel is enough.
                FarStep = pixelSize,
                WaterLevel = waterLevel,
                // Enough rays that neighbouring rays are less than a map pixel apart at full range.
                RayCount = Mathf.Clamp(Mathf.CeilToInt(fovRad * farRange / (pixelSize * 0.7f)) + 1, 8, 720),
                TextureSize = map.m_textureSize,
                Half = map.m_textureSize / 2,
                PixelSize = pixelSize,
                LineOfSight = _lineOfSight.Value,
                Forest = _forestEnabled.Value && _forestDensityMultiplier.Value > 0f,
                Horizon = horizonRange > range,
                ForestSight = _forestSightDistance.Value,
                Canopy = _canopyHeight.Value,
                // At sea, land has to rise above the sea. Inland it has to rise above the eye, against the sky.
                RiseBase = coastal ? waterLevel : Mathf.Max(eye.y, waterLevel),
                MinRiseSlope = Mathf.Tan((coastal ? _horizonMinAngle.Value : _horizonInlandMinAngle.Value) * Mathf.Deg2Rad),
            };
        }

        private void TraceRay(Sweep s, int r)
        {
            float t = s.RayCount == 1 ? 0.5f : (float)r / (s.RayCount - 1);
            // The far pass walks the near part again to rebuild line of sight; those cells are cached.
            TraceYaw(s, s.BaseYaw - s.FovRad * 0.5f + s.FovRad * t, -1f, s.FarPass ? s.FarRange : s.Range);
        }

        /// <summary>
        /// Walks one ray and explores every visible map pixel on it. With probeDist >= 0 nothing is
        /// explored; instead the verdict for the point at that distance is returned (calibration).
        /// </summary>
        private string TraceYaw(Sweep s, float yaw, float probeDist, float maxDist)
        {
            bool probe = probeDist >= 0f;
            if (probe && probeDist > maxDist)
                return $"beyond range ({maxDist:0} m)";

            float dx = Mathf.Sin(yaw);
            float dz = Mathf.Cos(yaw);
            Vector3 eye = s.Eye;
            float maxSlope = float.NegativeInfinity;
            float maxSlopeDist = 0f;
            // Meters of dense forest the ray has passed through at tree level.
            float forestDepth = 0f;
            // Once the forest is too thick, only points rising above the canopy line stay visible.
            float canopySlope = float.NegativeInfinity;

            for (float d = s.Step; d <= maxDist; d += d > s.Range ? s.FarStep : s.Step)
            {
                float x = eye.x + dx * d;
                float z = eye.z + dz * d;
                bool far = d > s.Range;
                bool atProbe = probe && d >= probeDist;

                if (s.LineOfSight || s.Forest || s.Horizon)
                {
                    Cell cell = GetCell(x, z, s.Step, s.WaterLevel);
                    float slope = (cell.Ground - eye.y) / d;

                    bool terrainHidden = false;
                    if (s.LineOfSight || s.Horizon)
                    {
                        // Distant coasts and peaks are only seen where nothing closer blocks them.
                        if (s.LineOfSight || far)
                            terrainHidden = slope < maxSlope;
                        if (slope > maxSlope)
                        {
                            maxSlope = slope;
                            maxSlopeDist = d;
                        }
                    }

                    // Too flat or too far away to stand out: water, low shores, distant lowlands.
                    bool tooFlat = far && cell.Height - s.RiseBase < d * s.MinRiseSlope;

                    bool forestHidden = false;
                    if (s.Forest)
                    {
                        forestHidden = forestDepth >= s.ForestSight && slope < canopySlope;

                        if (cell.Forest != 0f && eye.y - cell.Ground < s.Canopy
                            && GetCellForest(ref cell, x, z, s.Step) > 0f)
                        {
                            forestDepth += cell.Forest * (far ? s.FarStep : s.Step);
                            if (forestDepth >= s.ForestSight)
                                canopySlope = Mathf.Max(canopySlope, (cell.Ground + s.Canopy - eye.y) / d);
                        }
                    }

                    if (atProbe)
                    {
                        if (terrainHidden)
                            return $"hidden by terrain {maxSlopeDist:0} m away ({Mathf.Atan(maxSlope) * Mathf.Rad2Deg:0.0} deg, target {Mathf.Atan(slope) * Mathf.Rad2Deg:0.0} deg)";
                        if (tooFlat)
                            return $"too flat for horizon mode (needs {Mathf.Atan(s.MinRiseSlope) * Mathf.Rad2Deg:0.00} deg, has {Mathf.Atan2(cell.Height - s.RiseBase, d) * Mathf.Rad2Deg:0.00} deg)";
                        if (forestHidden)
                            return "hidden by forest";
                        return far ? "visible (horizon mode)" : "visible";
                    }

                    if (terrainHidden || tooFlat || forestHidden)
                        continue;
                }
                else if (atProbe)
                {
                    return "visible";
                }

                if (probe)
                    continue;

                int px = Mathf.RoundToInt(x / s.PixelSize + s.Half);
                int py = Mathf.RoundToInt(z / s.PixelSize + s.Half);
                if (px < 0 || py < 0 || px >= s.TextureSize || py >= s.TextureSize)
                    break;

                if (_explorePixel(s.Map, px, py))
                    s.Changed = true;
            }
            return probe ? "not reached" : null;
        }

        /// <summary>
        /// Reveals unexplored pixel groups within the fill radius that are fully enclosed by explored
        /// pixels and no larger than MaxGapSize. Groups touching the radius edge are left alone, so the
        /// frontier towards unexplored land never creeps outward.
        /// </summary>
        private bool FillSmallGaps(Minimap map, Vector3 center)
        {
            BitArray explored = _explored(map);
            int size = map.m_textureSize;
            if (explored == null || explored.Length < size * size)
                return false;

            float pixelSize = map.m_pixelSize;
            int half = size / 2;
            int cx = Mathf.RoundToInt(center.x / pixelSize + half);
            int cy = Mathf.RoundToInt(center.z / pixelSize + half);
            int r = Mathf.Max(1, Mathf.CeilToInt(_fillRadius.Value / pixelSize));
            int w = 2 * r + 1;
            int maxSize = _fillMaxHoleSize.Value;

            if (_fillVisited.Length < w * w)
                _fillVisited = new bool[w * w];
            else
                Array.Clear(_fillVisited, 0, w * w);

            bool changed = false;
            for (int ly = 0; ly < w; ly++)
            {
                for (int lx = 0; lx < w; lx++)
                {
                    int start = ly * w + lx;
                    if (_fillVisited[start] || !InFillArea(lx, ly, r, cx, cy, size))
                        continue;
                    if (explored[(cy + ly - r) * size + (cx + lx - r)])
                        continue;

                    // Flood-fill this unexplored group (4-connected).
                    _fillComponent.Clear();
                    _fillStack.Clear();
                    _fillVisited[start] = true;
                    _fillStack.Push(start);
                    bool enclosed = true;

                    while (_fillStack.Count > 0)
                    {
                        int cur = _fillStack.Pop();
                        int px = cur % w;
                        int py = cur / w;
                        if (_fillComponent.Count <= maxSize)
                            _fillComponent.Add(cur);
                        else
                            enclosed = false;

                        for (int n = 0; n < 4; n++)
                        {
                            int nx = px + (n == 0 ? 1 : n == 1 ? -1 : 0);
                            int ny = py + (n == 2 ? 1 : n == 3 ? -1 : 0);
                            if (!InFillArea(nx, ny, r, cx, cy, size))
                            {
                                enclosed = false; // reaches beyond the fill radius: not a gap
                                continue;
                            }
                            int ni = ny * w + nx;
                            if (_fillVisited[ni] || explored[(cy + ny - r) * size + (cx + nx - r)])
                                continue;
                            _fillVisited[ni] = true;
                            _fillStack.Push(ni);
                        }
                    }

                    if (!enclosed || _fillComponent.Count > maxSize)
                        continue;

                    foreach (int i in _fillComponent)
                    {
                        if (_explorePixel(map, cx + i % w - r, cy + i / w - r))
                            changed = true;
                    }
                }
            }
            return changed;
        }

        private static bool InFillArea(int lx, int ly, int r, int cx, int cy, int size)
        {
            int dx = lx - r;
            int dy = ly - r;
            if (dx * dx + dy * dy > r * r)
                return false;
            int gx = cx + dx;
            int gy = cy + dy;
            return gx >= 0 && gy >= 0 && gx < size && gy < size;
        }

        /// <summary>
        /// True while the player is on the open sea or within CoastDistance of it. Lakes and rivers do not count.
        /// </summary>
        private bool IsNearOcean(Vector3 pos)
        {
            WorldGenerator gen = WorldGenerator.instance;
            if (gen.GetBiome(pos.x, pos.z) == Heightmap.Biome.Ocean)
                return true;

            float radius = _horizonCoastDistance.Value;
            if (radius <= 0f)
                return false;

            // Sample two rings so narrow bays between the samples are not missed at short distances.
            for (int ring = 1; ring <= 2; ring++)
            {
                float dist = radius * ring * 0.5f;
                for (int i = 0; i < 12; i++)
                {
                    float a = i * (Mathf.PI * 2f / 12f);
                    if (gen.GetBiome(pos.x + Mathf.Sin(a) * dist, pos.z + Mathf.Cos(a) * dist) == Heightmap.Biome.Ocean)
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Follows the camera ray through the generated terrain and reports what it hits, next to the
        /// current fog and the view distances the mod would use, so the settings can be tuned to what
        /// is actually visible.
        /// </summary>
        private void Calibrate(Player player)
        {
            Camera cam = Utils.GetMainCamera();
            if (cam == null)
                return;

            WorldGenerator gen = WorldGenerator.instance;
            float waterLevel = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
            Vector3 origin = cam.transform.position;
            Vector3 dir = cam.transform.forward;
            Vector3 eye = player.m_eye != null ? player.m_eye.position : player.transform.position + Vector3.up * 1.7f;

            // March along the ray, then narrow the hit down by bisection.
            const float step = 2f;
            const float maxRay = 4000f;
            float hitT = -1f;
            for (float t = step; t <= maxRay; t += step)
            {
                Vector3 p = origin + dir * t;
                if (p.y > Mathf.Max(gen.GetHeight(p.x, p.z), waterLevel))
                    continue;
                float lo = t - step, hi = t;
                for (int i = 0; i < 12; i++)
                {
                    float mid = (lo + hi) * 0.5f;
                    Vector3 m = origin + dir * mid;
                    if (m.y > Mathf.Max(gen.GetHeight(m.x, m.z), waterLevel))
                        lo = mid;
                    else
                        hi = mid;
                }
                hitT = hi;
                break;
            }

            float fogRange = GetFogRange();
            float daylight = GetDaylight();
            float nightFactor = Mathf.Lerp(_nightMultiplier.Value, 1f, daylight);
            float heightBonus = GetHeightBonus(eye.y, waterLevel);
            float range = GetViewDistance(GetMaxDistance() * heightBonus, _minDistance.Value, _fogMultiplier.Value);
            bool nearOcean = IsNearOcean(player.transform.position);
            float horizonRange = GetViewDistance(_horizonMaxDistance.Value * heightBonus, 0f, _horizonFogMultiplier.Value);
            string env = EnvMan.instance?.GetCurrentEnvironment()?.m_name ?? "?";
            Vector3 pos = player.transform.position;

            string target;
            string summary;
            if (hitT < 0f)
            {
                target = "target=none (sky or beyond " + maxRay + " m)";
                summary = "No terrain in the crosshair";
            }
            else
            {
                Vector3 hit = origin + dir * hitT;
                float height = gen.GetHeight(hit.x, hit.z);
                bool water = height < waterLevel;
                float dist = new Vector2(hit.x - eye.x, hit.z - eye.z).magnitude;
                float rise = Mathf.Atan2(height - waterLevel, dist) * Mathf.Rad2Deg;
                float riseOverEye = Mathf.Atan2(height - Mathf.Max(eye.y, waterLevel), dist) * Mathf.Rad2Deg;
                // FogMultiplier values at which this point would just be inside the view distance.
                string needed = float.IsInfinity(fogRange) || nightFactor <= 0f
                    ? "n/a"
                    : (dist / (fogRange * nightFactor)).ToString("0.00");
                // Run the same ray the view cone uses towards this point, without exploring anything.
                string verdict = "n/a";
                string explored = "?";
                Minimap map = Minimap.instance;
                Sweep probe = map != null ? StartSweep(map, player, cam) : null;
                if (probe != null)
                {
                    verdict = TraceYaw(probe, Mathf.Atan2(hit.x - eye.x, hit.z - eye.z), dist, probe.FarRange);
                    BitArray bits = _explored?.Invoke(map);
                    int px = Mathf.RoundToInt(hit.x / probe.PixelSize + probe.Half);
                    int py = Mathf.RoundToInt(hit.z / probe.PixelSize + probe.Half);
                    if (bits != null && px >= 0 && py >= 0 && px < probe.TextureSize && py < probe.TextureSize)
                        explored = bits[py * probe.TextureSize + px] ? "yes" : "no";
                }
                target = $"target={(water ? "water" : "land")} dist={dist:0} height={height - waterLevel:0.0} rise={rise:0.000}deg riseOverEye={riseOverEye:0.000}deg " +
                         $"biome={gen.GetBiome(hit.x, hit.z)} at=({hit.x:0},{hit.z:0}) neededFogMultiplier={needed} " +
                         $"explored={explored} verdict=\"{verdict}\"";
                summary = $"{(water ? "Water" : "Land")} {dist:0} m away, {height - waterLevel:0} m above sea, {rise:0.00}°\n" +
                          $"Explored: {explored}, mod: {verdict}";
            }

            string fog = RenderSettings.fog
                ? $"fog={RenderSettings.fogMode} density={RenderSettings.fogDensity:0.00000} fogRange={fogRange:0}"
                : "fog=off";
            Logger.LogInfo($"[Calibrate] {target} | {fog} env={env} daylight={daylight:0.00} " +
                           $"viewRange={range:0} maxDistance={GetMaxDistance():0} heightBonus={heightBonus:0.00} coastal={nearOcean} horizonRange={horizonRange:0} " +
                           $"eyeHeight={eye.y - waterLevel:0.0} cameraAboveEye={origin.y - eye.y:0.0} pos=({pos.x:0},{pos.z:0})");
            player.Message(MessageHud.MessageType.TopLeft,
                $"{summary}\nView {range:0} m, horizon {horizonRange:0} m ({(nearOcean ? "coast" : "inland")}), fog {(float.IsInfinity(fogRange) ? "off" : fogRange.ToString("0") + " m")}");
        }

        /// <summary>
        /// Multiplier for the maximum distances: the higher your eye above the sea, the farther you can see.
        /// </summary>
        private float GetHeightBonus(float eyeY, float waterLevel)
        {
            float height = Mathf.Max(0f, eyeY - waterLevel);
            return 1f + Mathf.Min(height * _heightBonusPerMeter.Value, _heightBonusMax.Value) / 100f;
        }

        /// <summary>
        /// MaxDistance, or with 0 the game's simulation distance in meters as the graphics menu shows it.
        /// </summary>
        private float GetMaxDistance()
        {
            if (_maxDistance.Value > 0f)
                return _maxDistance.Value;
            if (ZNet.instance == null)
                return 288f;
            // Same formula as the graphics menu: zones around the player's zone, 32 m per half zone.
            int total = ZNet.instance.GetSyncedSimulationDistance().TotalSimulationDistance;
            return (2 * total + 1) * 32f;
        }

        /// <summary>
        /// Distance at which the fog swallows terrain completely, before FogMultiplier. Infinity without fog.
        /// </summary>
        private static float GetFogRange()
        {
            if (!RenderSettings.fog)
                return float.PositiveInfinity;

            float density = RenderSettings.fogDensity;
            switch (RenderSettings.fogMode)
            {
                case FogMode.Linear:
                    return RenderSettings.fogEndDistance;
                case FogMode.Exponential:
                    // Visibility ends where fog transmittance drops below ~5% (ln 20 ≈ 3).
                    return density > 0f ? 3f / density : float.PositiveInfinity;
                default:
                    return density > 0f ? 1.732f / density : float.PositiveInfinity;
            }
        }

        /// <summary>1 during the day, 0 at night, blended smoothly around dawn and dusk.</summary>
        private static float GetDaylight()
        {
            if (EnvMan.instance == null)
                return 1f;
            // Day fraction: 0 = midnight, 0.5 = noon. Blend around dawn (0.25) and dusk (0.75).
            float frac = EnvMan.instance.GetDayFraction();
            return Mathf.Clamp01((0.25f - Mathf.Abs(frac - 0.5f)) / 0.05f + 0.5f);
        }

        private float GetViewDistance(float max, float min, float fogMultiplier)
        {
            min = Mathf.Min(min, max);
            float range = max;

            if (_useFog.Value)
                range = Mathf.Min(range, GetFogRange() * fogMultiplier);

            range *= Mathf.Lerp(_nightMultiplier.Value, 1f, GetDaylight());

            return Mathf.Clamp(range, min, max);
        }

        private Cell GetCell(float x, float z, float size, float waterLevel)
        {
            long key = CellKey(x, z, size, out int cx, out int cz);
            if (!_cellCache.TryGetValue(key, out Cell cell))
            {
                float ground = WorldGenerator.instance.GetHeight((cx + 0.5f) * size, (cz + 0.5f) * size);
                cell.Height = ground;
                cell.Ground = Mathf.Max(ground, waterLevel);
                // Forest is only needed for cells near eye level, so it is computed on first use.
                cell.Forest = ground > waterLevel + 0.5f ? -1f : 0f;
                _cellCache[key] = cell;
            }
            return cell;
        }

        private float GetCellForest(ref Cell cell, float x, float z, float size)
        {
            if (cell.Forest < 0f)
            {
                long key = CellKey(x, z, size, out int cx, out int cz);
                cell.Forest = GetForestDensity((cx + 0.5f) * size, (cz + 0.5f) * size);
                _cellCache[key] = cell;
            }
            return cell.Forest;
        }

        private static long CellKey(float x, float z, float size, out int cx, out int cz)
        {
            cx = Mathf.FloorToInt(x / size);
            cz = Mathf.FloorToInt(z / size);
            return ((long)cx << 32) ^ (uint)cz;
        }

        private float GetForestDensity(float x, float z)
        {
            // Forest factor below the game's threshold means trees; lower = denser.
            float factor = WorldGenerator.GetForestFactor(new Vector3(x, 0f, z));
            float forestness = Mathf.Clamp01((ForestThreshold - factor) / 0.4f);

            float density;
            switch (WorldGenerator.instance.GetBiome(x, z))
            {
                case Heightmap.Biome.Meadows: density = forestness; break;
                case Heightmap.Biome.BlackForest: density = 0.5f + 0.5f * forestness; break;
                case Heightmap.Biome.Swamp: density = 0.3f + 0.3f * forestness; break;
                case Heightmap.Biome.Mistlands: density = 0.4f + 0.4f * forestness; break;
                case Heightmap.Biome.Mountain: density = 0.3f * forestness; break;
                case Heightmap.Biome.DeepNorth: density = 0.2f * forestness; break;
                case Heightmap.Biome.Plains: density = 0.15f * forestness; break;
                case Heightmap.Biome.AshLands: density = 0.1f * forestness; break;
                default: density = 0f; break;
            }
            return Mathf.Clamp01(density * _forestDensityMultiplier.Value);
        }
    }
}
