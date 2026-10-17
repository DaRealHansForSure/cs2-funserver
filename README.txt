CS2 LAN FUN SERVER
==================

A Counter-Strike 2 server for a LAN party, controlled by one Windows
program: CS2_SERVER_CONTROL.exe. The sister project of the CS 1.6 LAN Fun
Server, with the same window: live 3D map, server control, bots, maps,
stats and options.

STATUS: early version. The server side (install, start, stop, modes,
bots, RCON, live feed plugin) is built; it has not run against a real CS2
server yet.


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

  Classic Fun   Casual rules with bunny hopping; hook, grab and parachute
                follow with the plugin.
  Arms Race     Valve's GunGame: a new weapon with every kill.
  Deathmatch    Instant respawn.
  Zombie        Casual rules for now; the infection follows with a plugin.
  Deathrun      Casual rules on deathrun maps; the trapper logic follows.

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
