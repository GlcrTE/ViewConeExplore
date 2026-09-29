# View Cone Explore

A Valheim BepInEx mod that reveals the minimap only where you look. Vanilla uncovers a 100 m circle around you. With this mod, exploration follows your camera's view cone and real visibility: fog, rain and storms shorten the range, night reduces it, and hills and forests block your line of sight.

## Features

- **View cone exploration:** The map is revealed only where your camera looks, matching your actual field of view.
- **Dynamic view distance:** Fog, rain and storms shorten your range, and night reduces it.
- **Line of sight:** Hills and mountains hide what's behind them, while distant peaks stay visible.
- **Forests block your view:** Dense forest limits sight at ground level, but you can look over it from above. No physics, so it works at any distance.
- **Horizon mode:** Distant coastlines and peaks that you can just barely see are revealed far beyond the normal view distance. At sea and on the coast even low shores count; inland only tall peaks rising above you against the sky. Water and flat land stay hidden, and taller land shows up from farther away.
- **Height bonus:** Ground you look down on from a hill, a cliff or a mast stays recognizable farther away. The reach grows with the square root of how far your eye is above that ground: about 3x the draw distance from 16 m above, 7x from 90 m above. Fog and snowfall still limit it. Switch it with F7 (turning it on needs a second press within 3 seconds).
- **Gap filling:** Small unexplored spots near you are filled in automatically, so the map doesn't turn into a patchwork.
- **Near radius:** Your immediate surroundings stay revealed all around you.
- **Fully configurable and lightweight:** Client-side only, no Harmony patches, cached terrain data.

## Installation

1. Install [BepInExPack Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/).
2. Put `ViewConeExplore.dll` into `Valheim/BepInEx/plugins/`.
3. Start the game once. The config file is created at `BepInEx/config/valheim.viewconeexplore.cfg`.

The mod is client-side only and does not need to be installed on the server.

## Configuration

| Section | Setting | Default | Description |
|---|---|---|---|
| General | `Enabled` | `true` | Enable view-cone map exploration |
| Distance | `MaxDistance` | `0` | Maximum reveal distance in meters (clear weather, daytime). 0 = follow the game's simulation distance (Low 224 m, Medium/Classic 288 m, High 352 m, Very High 416 m, Ultra 480 m, Extreme 544 m) |
| Distance | `HeightBonusEnabled` | `true` | Height bonus on or off |
| Distance | `HeightBonusMaxFactor` | `8` | The height bonus reaches at most this many times the draw distance |
| Distance | `SnowVisibility` | `0.55` | Scales all fog-based distances while it is snowing |
| Distance | `MinDistance` | `0` | The reveal distance never drops below this (0 = follow the fog exactly) |
| Distance | `UseFogVisibility` | `true` | Derive the view distance from the current fog density |
| Distance | `FogMultiplier` | `2.2` | Scales the fog-derived view distance |
| Distance | `NightMultiplier` | `0.5` | View distance multiplier at night |
| Cone | `FieldOfView` | `0` | Horizontal cone angle in degrees (0 = camera FOV) |
| Cone | `LineOfSight` | `true` | Terrain blocks the view |
| Forest | `ForestBlocksView` | `true` | Forests block the view at tree level |
| Forest | `ForestSightDistance` | `50` | Meters you can see through dense forest |
| Forest | `CanopyHeight` | `20` | Tree height in meters |
| Forest | `DensityMultiplier` | `1.0` | Scales forest density (0 = no forests) |
| Horizon | `Enabled` | `true` | Reveal distant coasts and peaks beyond the normal view distance |
| Horizon | `MaxDistance` | `1500` | Maximum distance for coasts and peaks (clear weather, daytime) |
| Horizon | `FogMultiplier` | `3.5` | Scales the fog-derived distance for coasts and peaks |
| Horizon | `MinAngle` | `0.25` | At sea or on the coast: how far land must rise above the sea to be noticed, in degrees (0.25 ≈ 4.4 m at 1000 m) |
| Horizon | `InlandMinAngle` | `1.5` | Elsewhere: how far a peak must rise above your eye level, in degrees (1.5 ≈ 26 m at 1000 m; 0 = coast only) |
| Horizon | `CoastDistance` | `60` | `MinAngle` applies while open ocean is within this many meters, `InlandMinAngle` elsewhere |
| GapFill | `FillSmallGaps` | `true` | Fill small enclosed gaps near you |
| GapFill | `Radius` | `100` | Radius in meters for gap filling |
| GapFill | `MaxGapSize` | `12` | Largest gap filled, in map pixels (1 px = 12 x 12 m) |
| Vanilla | `NearRadius` | `15` | Vanilla all-around explore radius (vanilla default: 100) |
| Performance | `UpdateInterval` | `0.5` | Seconds between updates |
| Performance | `FrameBudgetMs` | `2` | Milliseconds per frame for the view cone; updates are spread over several frames |
| Debug | `CalibrationKey` | `F8` | Measures the terrain in the crosshair and logs distance, height, fog and view distances |

## Compatibility

- Not compatible with mods that enlarge the vanilla explore radius, such as Bigger Minimap Discovery Radius or the `[Map] exploreRadius` setting in Valheim Plus. They reveal a full circle around you again.
- Forests are based on the procedurally generated world, so trees you cut down still count as forest.

## Building

Requires the .NET SDK. The project references the game's assemblies relative to its location and expects to sit in `Valheim/BepInEx/src/ViewConeExplore/`. From anywhere else, pass the game folder:

```
dotnet build -c Release -p:GameDir="C:/Path/To/Valheim/"
```

After a successful build, the DLL is copied to `BepInEx/plugins/` automatically.

## Credits

Thanks to Tenson for *Bigger Minimap Discovery Radius* and Slothsoft for *Better Exploration*. Their ideas of tying map discovery to weather, time of day and line of sight inspired this mod.

## License

[MIT](LICENSE)
