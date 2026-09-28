# Changelog

## 1.2.0

### Added
- **Height bonus:** The higher your eye is above the sea, the farther you can see. By default each meter adds 1% to the maximum view distance and the horizon range, up to +100% (`[Distance] HeightBonusPerMeter`, `HeightBonusMax`). On Extreme (544 m), that is about 555 m on deck, 625 m 15 m up, and 870 m on a 60 m hill. Fog still limits the view, so height only helps in clear weather.
- The calibration key now also shows whether the target is explored on the map and, if the mod considers it hidden, why (terrain, forest, too flat, out of range).

### Fixed
- In new areas with a long horizon range, the map could take many seconds to update. Each update now reveals the normal view range for the whole cone first and the far band afterwards, and the map refreshes during long updates instead of only at the end.

## 1.1.0

### Added
- **Horizon mode:** Distant coastlines and peaks that you can just barely see are now revealed far beyond the normal view distance, up to 1500 m in clear weather. Fog shortens the range, but silhouettes stay visible much deeper into it than terrain details.
  - At sea and on the coast (open ocean within 60 m), even low shores are revealed once they rise far enough above the sea (`MinAngle`, 0.25°).
  - Inland, only tall peaks that rise above your eye level against the sky are revealed (`InlandMinAngle`, 1.5°). Set it to 0 to use horizon mode only at sea.
  - Water, flat land and anything hidden behind closer terrain stays unexplored.
- **Draw distance sync:** `MaxDistance = 0` (the new default) follows the game's simulation distance setting: Low 224 m, Medium/Classic 288 m, High 352 m, Very High 416 m, Ultra 480 m, Extreme 544 m. In multiplayer, a lower limit set by the server applies.
- **Calibration key (F8):** Aim at terrain and press the key to see its distance, height and angle together with the current fog and the view distances the mod uses. Details are written to the BepInEx log.
- New settings: `[Horizon] Enabled, MaxDistance, FogMultiplier, MinAngle, InlandMinAngle, CoastDistance`, `[Performance] FrameBudgetMs`, `[Debug] CalibrationKey`.

### Changed
- Default `FogMultiplier` raised from 1.8 to 2.2. All view distance defaults are now calibrated against in-game measurements of what is actually still visible in fog.
- Default `MaxDistance` changed from 400 to 0 (follow the game's simulation distance).

### Performance
- View cone updates are spread over several frames within a per-frame time budget (`FrameBudgetMs`, 2 ms), so long view distances no longer cause stutters.
- Terrain beyond the normal view distance is sampled once per map pixel, and forest density is only computed where it can block the view.

### Upgrading
BepInEx keeps the values in your existing config file. To get the new defaults, set `MaxDistance = 0` and `FogMultiplier = 2.2` in `BepInEx/config/valheim.viewconeexplore.cfg`, or delete the file and let the game recreate it.

## 1.0.1

### Changed
- Default `FogMultiplier` raised from 1.0 to 1.8 and `MinDistance` from 40 to 80, so coasts and rocks that are faintly visible through fog (for example at sea) get revealed.

## 1.0.0

- Initial release.
