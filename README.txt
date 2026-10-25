CS2 LAN FUN SERVER
==================

A Counter-Strike 2 server for a LAN party, controlled by one Windows
program: CS2_SERVER_CONTROL.exe. The sister project of the CS 1.6 LAN Fun
Server, with the same window: live 3D map, server control, bots, maps,
stats and options.

STATUS: early version. Install, start, stop, all five modes, bots, the live
3D map, the fun plugin and Workshop maps run on a real CS2 server; the
in-game feel is still being tested.


WHAT EVERY PC NEEDS
-------------------

Server PC: Windows 10 or 11, 4+ CPU cores, 8 GB RAM, about 70 GB free disk
space (SSD), internet for the install and for Steam.

Every player: a PC that runs CS2 (Windows 10, 4-core CPU, 8 GB RAM, a
DirectX 11 graphics card with 1 GB VRAM, about 85 GB disk), CS2 installed
through Steam before the party, and internet at the party (CS2 and the
Steam Workshop need Steam online).


GETTING STARTED
---------------

1. Unpack the folder to a permanent location, e.g. D:\Games\CS2-FunServer.

2. Start CS2_SERVER_CONTROL.exe. Before the first install it opens on the
   Install screen. Press "Install or repair". It downloads and sets up:
   - the CS2 dedicated server through SteamCMD (about 60 GB),
   - Metamod:Source and CounterStrikeSharp (the plugin system),
   - the FunServer plugin and the configs of this package.

3. Press Start on the Server screen. The bar at the top shows the address,
   e.g. 192.168.1.50:27015. Players join with the console command
     connect 192.168.1.50:27015


MODES
-----

  Classic Fun   Casual rules with bunny hopping, hook, grab, parachute and
                a fun round every few rounds.
  Arms Race     Valve's GunGame: a new weapon with every kill.
  Deathmatch    Instant respawn, with the hook.
  Zombie        Everybody starts human; after 10 seconds the first zombies
                turn. A zombie hit infects, only the last human really dies.
                Zombies glow red, humans blue, through walls.
  Deathrun      One trapper against everybody, knives only. Needs deathrun
                maps from the Steam Workshop: add them on the Maps screen,
                then pick one with the Deathrun mode on the Server screen.

To change the mode during the party, type !mode in chat: everybody votes
for 20 seconds by typing the number of a mode.
Type !help in chat for the rules of the running mode and the controls.


WORKSHOP MAPS
-------------

Maps screen, Workshop tab: "Browse the Workshop" opens Steam's Workshop in
the browser. Copy the address of a map page, paste it into the box and
press Add. The map then shows on the Server screen in every mode. The
server downloads it the first time it starts it (a few seconds to a
minute), the players' CS2 downloads it by itself when they join.
Workshop maps are played by pick; the rotation stays on Valve's maps.


FOR THE PLAYERS (no binds needed)
---------------------------------

  Hook        Hold F (inspect weapon) while aiming at a wall: a glowing
              rope pulls you there. Classic and Deathmatch.
  Parachute   Hold E while falling.
  Grab        Admins hold E while aiming at a player to lift them, left
              click throws. As long as nobody is listed in
              addons\counterstrikesharp\configs\admins.json, everybody is admin.
  Streaks     Every 5 kills in a row: a gold glow and +50 HP; at 10 also a
              rocket jump.
  Fun rounds  Every third round in Classic: knives only, AWP only, low
              gravity, speed, headshots only, one bullet Deagle, HE war,
              ghosts or a Juggernaut. The host can force one on the Server
              screen.

Mode settings live in server\game\csgo\cfg\fun\modes\. Your own changes
from the window go into local*.cfg files next to them and survive every
install.


AFTER A CS2 UPDATE
------------------

Valve updates CS2 often. After an update the plugin system (Metamod,
CounterStrikeSharp) may need a newer version. The window puts the Metamod
line back into gameinfo.gi before every start; if the fun plugins still do
not load, run "Install or repair" again, or wait for a CounterStrikeSharp
update. Arms Race, Deathmatch and Casual also work without plugins.
