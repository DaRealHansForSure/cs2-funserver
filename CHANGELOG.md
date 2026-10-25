# Changelog

Every release of the CS2 LAN Fun Server. The project follows
[Semantic Versioning](https://semver.org/); while the major version is 0,
a minor release may change configs and screens.

## [0.4.0] - 2026-10-25

### Added

- In-game texts: welcome line, `!help`, rotating tips and center lines for
  every role.
- Map pictures from the game's own map vote screenshots.
- Leaderboard with headshots and best streak, and a player details dialog.
- README with badges, MIT license and this changelog.

### Changed

- Zombie bots stay rogue, so they hunt the humans instead of retreating.
- Console Follow stays off after a click; only a scroll of the user turns
  it back on.
- Code style: no multi-line or nested conditional expressions, and an
  `.editorconfig` that states the layout (four spaces, K&R braces).

### Fixed

- Zombie: the last human dies for real, and a round with no human left
  always ends.
- A freshly unpacked package is found by its `server-files` folder, not by
  a `tools` folder further up.
- The Admins error names the file, the exception type and the code.
- No null dereference when a game package lists no entries.

## [0.3.0] - 2026-10-18

### Added

- Fun plugin: hook, grab, parachute, fun rounds, kill streaks and the
  `!mode` vote.
- Zombie infection, the deathrun trapper and an outline glow through walls
  (zombies red, humans blue).
- Map rotation from the mode lists; next map and time left through the
  live feed.
- Admin list on the Install screen (CounterStrikeSharp `admins.json`).
- Stats from the plugin's own evening log, bot skills from the game
  package.
- Workshop maps: details by link, downloads on the server, Live Map and
  Server screen support, and a Workshop tab on the Maps screen.

### Changed

- Bots may use the AWP by default.
- Bot settings that CS2 does not know are gone from the Bots screen.

## [0.2.0] - 2026-10-17

### Added

- CS2 map wireframes for the Live Map from the world collision, kill feed
  icons from the game's SVGs.
- CSTV instead of HLTV, CS2 firewall rules, the glow in the live feed.
- The Bots screen says when the bots wait for a human; a default server
  name in `base.cfg`.

### Changed

- The server runs on a hidden desktop, so its console never takes the
  focus.

### Removed

- The CS 1.6 leftovers: nav builder, map pack, market, admin prompt and
  fast download.

### Fixed

- Verified against a real CS2 server: no hibernation on an empty server,
  the console log path, `mp_restartgame` instead of `sv_restart`, rocket
  through Teleport.

## [0.1.0] - 2026-10-17

### Added

- The CS 1.6 Server Control app, ported to CS2: paths, `cs2.exe` start
  and Source RCON.
- Installer: SteamCMD app 730, Metamod:Source and CounterStrikeSharp,
  pinned to exact versions.
- The FunServer plugin, the mode configs, the build and the docs.
- The first install copies the CS2 of the local Steam library instead of
  downloading 60 GB.

[0.4.0]: https://github.com/DaRealHansForSure/CS2-FunServer/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/DaRealHansForSure/CS2-FunServer/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/DaRealHansForSure/CS2-FunServer/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/DaRealHansForSure/CS2-FunServer/releases/tag/v0.1.0
