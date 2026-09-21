# Hoard

Seal a chest so that container automation mods leave it alone.

Mods that store items into nearby chests, craft from them or feed tames from them usually treat every chest you own the same way. Their own settings can at most exclude a whole chest type. Hoard lets you take one particular chest out of their reach.

![A sealed chest in Hoard](https://raw.githubusercontent.com/isimp/Hoard/main/docs/images/screenshot.webp)

## AI notice

Most of Hoard was written by Claude Code (Anthropic), which did the heavy lifting on implementation and design. Heads-up so you can judge for yourself.

## Using it

Look at a chest and press K to seal it, and press K again to unseal it. The chest's hover text shows whether it is sealed. The seal is stored on the chest itself, so it lasts across saves and is the same for every player. Inside a ward only players permitted on that ward can change it, and a personal chest can only be sealed by the player who built it.

Sealed chests can also give off a faint glow so they stand out without hovering. It is off by default and is switched on with ShowSealGlow.

Hoard finds supported mods on its own and does nothing if none are installed. Type /hoard in chat to see what it found. If a supported mod is updated in a way Hoard no longer recognises, Hoard tells you when you spawn, because that mod ignores seals until Hoard supports it again.

Tested with AzuAutoStore, AzuCraftyBoxes, GrabMaterials and PetPantry.

## SeidrChest

SeidrChest keeps its bound chest loaded at its real position, so while you are at base the automation mods treat it as an ordinary nearby chest. Turn on AutoSealSeidrChest and Hoard seals the currently bound chest automatically. The seal lifts again when the chest is unbound. The setting is off by default.

## Multiplayer

Hoard has to be installed on the server and by every player, and the server refuses players without it. Automation mods run on each player's own machine, so a single player without Hoard could still empty a sealed chest. The server's settings apply to everyone, while your key and hover text options stay your own.

## Settings

All settings are in BepInEx/config/isimp.Hoard.cfg, each with a description. Every supported mod Hoard finds gets its own switch there.

## More

Technical notes and build instructions are on GitHub at https://github.com/isimp/Hoard
