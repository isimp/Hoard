# Technical notes

## How it works

Valheim has no container registry and no way to exclude a single chest. `Container.GetInventory()` comes closest, but the game calls it from many places, some of them every frame, mods also read the private inventory field directly, and it cannot tell storing items apart from displaying them.

What container automation mods have in common is the registry each one builds for itself: a type holding a static collection of `Container`, and a static `void` method on that type taking exactly one `Container`. Hoard scans the loaded plugin assemblies for that shape at the main menu and adds a prefix to every match, so a sealed chest never enters the list. That covers every feature built on the list, including ones a mod adds later. Names are not used to decide what is included, only to tell adding from removing, so a method with an unexpected name is still found and starts out switched off.

Each match gets its own switch under `Targets` in the config. A patch that fails is logged and skipped, and that mod keeps its normal behaviour.

The seal is a value on the chest's ZDO, which the game saves and replicates like any other. Removing the mod leaves nothing behind but that unread value.

`AutoSealSeidrChest` does not write a seal. It matches the name SeidrChest gives a chest while it is bound, so the rule stops applying as soon as the chest is unbound.

## Server rules

On a server, Hoard is required through ServerSync and clients without it are refused. `Enabled`, `AutoSealSeidrChest`, `DefaultOffAssemblies` and every target switch are pushed from the server while `LockConfiguration` is on, and admins can still change them. `MarkKey`, `MarkModifier` and `ShowHoverHint` are never synchronised. A server rule for a mod a player does not have is ignored on that player's machine.

## Limits

Mods that store containers in another form, or register them from an instance method, are not found. For a mod with no removal method, sealing takes effect there only after the chest reloads. The server cannot enforce a seal itself, because the automation mods run on each client and the server only sees the resulting inventory change, which is why every client needs Hoard.

## Building

Requires the .NET SDK 8 or newer. To build against the reference stubs in `lib/`, with no game installation needed:

```
dotnet build -c Release -p:LibsDir=lib
```

To build against a local installation and copy the result into a BepInEx profile:

```
dotnet build -c Release -p:ValheimDir="<Valheim folder>" -p:ProfileDir="<profile folder>"
```

The `VALHEIM_DIR` environment variable can be used instead of `ValheimDir`. Without these, a default Steam installation and a default Gale profile are assumed.

The files in `lib/` contain metadata only: every method body is replaced and resources are removed, so they can be compiled against but not run. Regenerate them after a game update with `tools/strip-references.ps1`. Releasing is described in [releasing.md](releasing.md).

ServerSync by blaxxun is included in `src/ServerSync` under MIT-0.
