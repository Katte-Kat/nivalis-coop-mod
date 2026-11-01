# Nivalis Coop mod

A BepInEx plugin that adds **Multiplayer** to Nivalis Nights: a server list in the main menu, joining a server's
shared world (or creating one), an in-game chat, and shared money, conversations and time. The server lives in the
`nivalis-coop-server` repository.

What is shared on a server: the world save, money (one account for all), businesses, placed furniture, the people
(see below), the other players as figures, conversations (everybody sees
the same one and votes on the answer; a disagreement is drawn at random after 5 s) and the world clock (a
conversation pauses time for everybody). Inventories stay per player.

## Install (players)

1. Install **BepInEx 6.0.0-pre.2, IL2CPP, win-x64** into the game folder (download it from the official BepInEx
   release page; it is not redistributed here). Start the game once so BepInEx creates `BepInEx/interop`, then quit.
2. Unzip the release into the game folder, so that you get
   `BepInEx/plugins/NivalisCoop/NivalisCoop.dll` and `NivalisCoop.Net.dll`.
3. Start the game. In the main menu choose **Multiplayer**, add the server address (`host:27800`), name and password.
   F8 opens the same window in game.

On Linux with Proton set the launch option `WINEDLLOVERRIDES="winhttp=n,b" %command%` so the BepInEx loader is used.

## Use

| Key | Action |
| --- | --- |
| Enter | open the chat (in the game world, outside conversations) |
| T | open the chat everywhere |
| F8 | multiplayer window |

Settings are in `BepInEx/config/dev.katte.nivaliscoop.cfg` (server address, password, name, keys, money sync).

A server world is never saved into your normal single-player saves: the game's saves and autosaves are stopped
while you are in one, and the session leader's game stores the world on the server instead. The temporary
`COOP_<world>.sav` the transfer needs is removed again right away.

People (customers, staff, NPCs) are simulated by one game per world, the session leader: the first player in the
world, and when they leave, the next one, by itself. The other games show the people where the leader has them.

## Build

Needs the .NET SDK and a game folder with BepInEx installed and run once.

```bash
export NIVALIS_GAME_DIR="/path/to/Nivalis Nights"
./build-plugin.sh install          # builds and copies into the game's BepInEx/plugins
./package.sh                       # dist/NivalisCoop-<version>.zip for players
```

`NivalisCoop.Net` is the protocol client (no game dependencies); `NivalisCoop.NetTest` is an end-to-end test that
needs a running server: `dotnet run --project NivalisCoop.NetTest -- 127.0.0.1 27800`.
`tools/InteropDump` lists types and members of the generated interop assemblies, handy for new patches.

## License

MIT, see [LICENSE](LICENSE).
