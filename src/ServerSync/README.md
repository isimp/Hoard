# ServerSync (vendored)

`ConfigSync.cs` is **not ours**. It is blaxxun's ServerSync, taken unmodified from

    https://raw.githubusercontent.com/blaxxun-boop/ServerSync/master/ConfigSync.cs

fetched 2026-09-17. Licence: **MIT-0** — public-domain-equivalent, no attribution required, so it
may simply be compiled in. It is the way most Valheim mods sync config: 32 of the mods in the
profile this was built against embed the same file.

It is vendored as source rather than referenced as a package on purpose. Compiled in, Sparring
stays a single DLL with nothing to install alongside it; as a dependency it would oblige everyone
running the mod to fetch a library as well.

**Keep it unmodified.** Updating should be a wholesale replacement of that one file. What it needs
beyond itself lives in `Sparring.csproj`, not in the file:

- `<Nullable>annotations</Nullable>`, so its `?` annotations parse without turning null-state
  warnings on for the rest of the project.
- `System.IO.Compression`, which it uses to compress config payloads.
- `Unity.TextMeshPro`, for the one place it rewrites the connect-error screen.

It also wants JetBrains's `[PublicAPI]` and `[UsedImplicitly]`, and those need nothing: Unity ships
both in `UnityEngine.CoreModule`. A hand-written stub for them compiles but collides with Unity's,
so don't add one.

What it does, from reading it before it went in: it patches `ZNet.Awake`, `OnNewConnection`,
`RPC_PeerInfo`, `Shutdown`, `ZRpc.HandlePackage` and `ConfigEntryBase.Get/SetSerializedValue`,
registers its own routed RPC per mod, and briefly wraps the connection socket so config arrives
before a client finishes joining. It touches no files beyond BepInEx's own `configFile.Save()`,
opens no sockets of its own, and makes no web requests. Admin status is decided server-side from
Valheim's admin list and sent to the client as a `lockexempt` flag.
