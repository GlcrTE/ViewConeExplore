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
        public const string Version = "1.0.1";

        private ConfigEntry<bool> _enabled;
        private ConfigEntry<float> _maxDistance;
        private ConfigEntry<float> _minDistance;
        private ConfigEntry<bool> _useFog;
        private ConfigEntry<float> _fogMultiplier;
        private ConfigEntry<float> _nightMultiplier;
        private ConfigEntry<float> _fov;
        private ConfigEntry<float> _nearRadius;
        private ConfigEntry<bool> _lineOfSight;
        private ConfigEntry<float> _interval;
        private ConfigEntry<bool> _forestEnabled;
        private ConfigEntry<float> _forestSightDistance;
        private ConfigEntry<float> _canopyHeight;
        private ConfigEntry<float> _forestDensityMultiplier;
        private ConfigEntry<bool> _fillEnabled;
        private ConfigEntry<float> _fillRadius;
        private ConfigEntry<int> _fillMaxHoleSize;

        private Func<Minimap, int, int, bool> _explorePixel;
        private AccessTools.FieldRef<Minimap, Texture2D> _fogTexture;
        private AccessTools.FieldRef<Minimap, BitArray> _explored;

        // Scratch buffers for hole filling, reused between updates.
        private bool[] _fillVisited = new bool[0];
        private readonly List<int> _fillComponent = new List<int>();
        private readonly Stack<int> _fillStack = new Stack<int>();

        private struct Cell
        {
            public float Ground;  // terrain height, raised to water level over water
            public float Forest;  // 0 = open, 1 = dense forest
        }

        // World heights and forests are procedural and never change, so caching them is safe.
        private readonly Dictionary<long, Cell> _cellCache = new Dictionary<long, Cell>();
        private const int MaxCacheEntries = 250000;
        // Same threshold the game uses for WorldGenerator.InForest().
        private const float ForestThreshold = 1.15f;
        private float _timer;

        private void Awake()
        {
            _enabled = Config.Bind("General", "Enabled", true, "Enable view-cone map exploration.");
            _maxDistance = Config.Bind("Distance", "MaxDistance", 400f,
                new ConfigDescription("Maximum reveal distance in meters (clear weather, daytime).", new AcceptableValueRange<float>(50f, 2000f)));
            _minDistance = Config.Bind("Distance", "MinDistance", 80f,
                new ConfigDescription("Reveal distance never drops below this (thick fog, night).", new AcceptableValueRange<float>(0f, 500f)));
            _useFog = Config.Bind("Distance", "UseFogVisibility", true,
                "Derive the view distance from the current fog density (rain, mist, storms shorten it).");
            // 1.8 roughly matches where silhouettes (coasts, rocks) still show through the fog.
            _fogMultiplier = Config.Bind("Distance", "FogMultiplier", 1.8f,
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

            _timer += Time.deltaTime;
            if (_timer < _interval.Value)
                return;
            _timer = 0f;

            if (player.IsDead() || player.InInterior())
                return;

            Camera cam = Utils.GetMainCamera();
            if (cam == null)
                return;

            RevealViewCone(map, player, cam);
        }

        private void RevealViewCone(Minimap map, Player player, Camera cam)
        {
            float range = GetViewDistance();
            if (range <= 0f)
                return;

            Vector3 eye = player.m_eye != null ? player.m_eye.position : player.transform.position + Vector3.up * 1.7f;

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
            float step = pixelSize * 0.5f;
            // Enough rays that neighbouring rays are less than a map pixel apart at full range.
            int rayCount = Mathf.Clamp(Mathf.CeilToInt(fovRad * range / (pixelSize * 0.7f)) + 1, 8, 720);

            float baseYaw = Mathf.Atan2(forward.x, forward.z);
            float waterLevel = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
            int textureSize = map.m_textureSize;
            int half = textureSize / 2;
            bool lineOfSight = _lineOfSight.Value;
            bool forest = _forestEnabled.Value && _forestDensityMultiplier.Value > 0f;
            float forestSight = _forestSightDistance.Value;
            float canopy = _canopyHeight.Value;
            bool changed = false;

            if (_cellCache.Count > MaxCacheEntries)
                _cellCache.Clear();

            for (int r = 0; r < rayCount; r++)
            {
                float t = rayCount == 1 ? 0.5f : (float)r / (rayCount - 1);
                float yaw = baseYaw - fovRad * 0.5f + fovRad * t;
                float dx = Mathf.Sin(yaw);
                float dz = Mathf.Cos(yaw);
                float maxSlope = float.NegativeInfinity;
                // Meters of dense forest the ray has passed through at tree level.
                float forestDepth = 0f;
                // Once the forest is too thick, only points rising above the canopy line stay visible.
                float canopySlope = float.NegativeInfinity;

                for (float d = step; d <= range; d += step)
                {
                    float x = eye.x + dx * d;
                    float z = eye.z + dz * d;

                    if (lineOfSight || forest)
                    {
                        Cell cell = GetCell(x, z, step, waterLevel);
                        float slope = (cell.Ground - eye.y) / d;

                        bool hidden = false;
                        if (lineOfSight)
                        {
                            hidden = slope < maxSlope;
                            maxSlope = Mathf.Max(maxSlope, slope);
                        }

                        if (forest)
                        {
                            if (forestDepth >= forestSight && slope < canopySlope)
                                hidden = true;

                            if (cell.Forest > 0f && eye.y - cell.Ground < canopy)
                            {
                                forestDepth += cell.Forest * step;
                                if (forestDepth >= forestSight)
                                    canopySlope = Mathf.Max(canopySlope, (cell.Ground + canopy - eye.y) / d);
                            }
                        }

                        if (hidden)
                            continue;
                    }

                    int px = Mathf.RoundToInt(x / pixelSize + half);
                    int py = Mathf.RoundToInt(z / pixelSize + half);
                    if (px < 0 || py < 0 || px >= textureSize || py >= textureSize)
                        break;

                    if (_explorePixel(map, px, py))
                        changed = true;
                }
            }

            if (_fillEnabled.Value && _explored != null && FillSmallGaps(map, player.transform.position))
                changed = true;

            if (changed)
                _fogTexture(map).Apply();
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

        private float GetViewDistance()
        {
            float max = _maxDistance.Value;
            float min = Mathf.Min(_minDistance.Value, max);
            float range = max;

            if (_useFog.Value && RenderSettings.fog)
            {
                float density = RenderSettings.fogDensity;
                float fogRange;
                switch (RenderSettings.fogMode)
                {
                    case FogMode.Linear:
                        fogRange = RenderSettings.fogEndDistance;
                        break;
                    case FogMode.Exponential:
                        // Visibility ends where fog transmittance drops below ~5% (ln 20 ≈ 3).
                        fogRange = density > 0f ? 3f / density : max;
                        break;
                    default:
                        fogRange = density > 0f ? 1.732f / density : max;
                        break;
                }
                range = Mathf.Min(range, fogRange * _fogMultiplier.Value);
            }

            if (EnvMan.instance != null)
            {
                // Day fraction: 0 = midnight, 0.5 = noon. Blend smoothly around dawn (0.25) and dusk (0.75).
                float frac = EnvMan.instance.GetDayFraction();
                float daylight = Mathf.Clamp01((0.25f - Mathf.Abs(frac - 0.5f)) / 0.05f + 0.5f);
                range *= Mathf.Lerp(_nightMultiplier.Value, 1f, daylight);
            }

            return Mathf.Clamp(range, min, max);
        }

        private Cell GetCell(float x, float z, float size, float waterLevel)
        {
            int cx = Mathf.FloorToInt(x / size);
            int cz = Mathf.FloorToInt(z / size);
            long key = ((long)cx << 32) ^ (uint)cz;
            if (!_cellCache.TryGetValue(key, out Cell cell))
            {
                float wx = (cx + 0.5f) * size;
                float wz = (cz + 0.5f) * size;
                float ground = WorldGenerator.instance.GetHeight(wx, wz);
                cell.Ground = Mathf.Max(ground, waterLevel);
                cell.Forest = ground > waterLevel + 0.5f ? GetForestDensity(wx, wz) : 0f;
                _cellCache[key] = cell;
            }
            return cell;
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
