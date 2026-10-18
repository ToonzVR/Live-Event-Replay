Monster Event Watch v0.3.3 — Meta PCVR

Fresh source build for sharing. Includes the v0.3.2 ship-rotation fix, hidden watch, solo playback and private-code synchronized starts. This release adds no new gameplay changes.

INSTALL
Close Gorilla Tag. Copy MonsterEventWatch.dll into your Meta PCVR Gorilla Tag installation's BepInEx/plugins folder. BepInEx must already be installed. Remove older copies of this plugin before installing. Restart the game and look for REPLAY v0.3.3.

SOLO
Leave multiplayer, walk into City, and touch the left-wrist watch with your right hand. Playback begins after five seconds.

WITH FRIENDS
Everyone installs this version, joins the same PRIVATE code, and enters City. Wait a few seconds for readiness, then have one player press the watch. Ready mod users start together after approximately seven seconds. The ship teleport occurs 18 seconds into playback. Public matchmaking and late joining a running replay are not supported.

The watch hides during playback and returns at the end. F8 cancels your local replay. Leaving/changing rooms cancels local playback.

BUILD CHECKS
Freshly compiled from the included source: zero warnings/errors. Inspected every compiled type and method: zero embedded resources, no module initializer, and no calls that load binary payloads or set creator codes. The known Harmony.PatchInfo.bin name is absent in ASCII and UTF-16. The package contains only this mod DLL, source, documentation and checksums; it includes no Harmony/game DLLs or old backups. All 23 shared-start protocol checks passed.

These checks establish the contents of this release, not the safety of other installed mods. Full VR event playback and multi-headset core interactions remain unverified. Check SHA256SUMS.txt if you want to confirm the distributed DLL matches this build.

SOURCE BUILD
Requires a compatible .NET SDK, Meta PCVR Gorilla Tag and BepInEx:
dotnet build source/MonsterEventWatch.csproj -c Release -p:GameDir="YOUR META GORILLA TAG INSTALL DIRECTORY"

UNINSTALL
Close the game and remove MonsterEventWatch.dll from BepInEx/plugins.
