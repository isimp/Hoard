# Releasing

Pushing builds the mod. Releasing is a manual workflow that publishes to GitHub and to [Hexium](https://valheim.hexium.gg/).

## One-time setup

The icon lives at `packaging/icon.png`: a 256x256 PNG, committed to the repository. The release workflow stops if it is missing or the wrong size.

Create an API token in the Hexium team settings (the settings icon next to your username, then the bottom of the page) and store it in this repository as the secret `HEXIUM_TOKEN`. The release workflow checks for it before building.

## Cutting a release

1. Set the new version in `Hoard.csproj` (`<Version>`), `src/Plugin.cs` (`public const string Version`) and `packaging/manifest.json` (`version_number`). The workflow checks that all three agree.
2. Add a CHANGELOG entry. It ships in the package and appears on the mod page.
3. Push, and wait for the Build workflow to pass.
4. Run the Release workflow from the Actions tab with the version (without `v`), optional release notes, the draft flag, the Hexium team and the category slugs.

The workflow then runs these steps in order:

| Step | |
|---|---|
| Version check | All three version sources match the input. |
| Icon check | `packaging/icon.png` exists. |
| Token check | `HEXIUM_TOKEN` is set. |
| Build | Against `lib/`, so no game install is needed. |
| Package | `Hoard-X.Y.Z.zip` with the manifest, icon, README, CHANGELOG, LICENSE and the DLL. |
| Package check | Hexium's rules: required files at the root, icon exactly 256x256, name of letters, digits and underscores, description of at most 256 characters. |
| GitHub release | Tags `vX.Y.Z` and attaches the zip and the DLL. |
| Hexium | Uploads the package and submits the version. |

If the Hexium step fails, fix the cause and run the workflow again with the same version. The GitHub release is updated rather than duplicated. Hexium refuses a version that already exists.

## Install location and compatibility

Hoard is needed on the server and on every client. It sets `ModRequired`, so a server running it refuses players without it, because the automation mods run on each client. This is worth stating on the mod page.

`MinimumVersion` in `src/Plugin.cs` is the oldest version a peer may run. Hoard has no RPCs, and the only thing clients must agree on is the ZDO key, so it only needs raising if that key changes.

## Checking discovery before a release

Hoard patches methods in other mods, found at runtime. A target that fails to patch is logged and skipped, and that mod keeps its normal behaviour. Before a release, launch the game once and check the log, which lists every registration point found and every patch installed:

```
[Info   :     Hoard] Scanned N assemblies in Nms, found N container registration point(s).
[Info   :     Hoard]   AzuAutoStore::AzuAutoStore.Util.Boxes::AddContainer  (evict: RemoveContainer)
[Info   :     Hoard] Sealing honoured by AzuAutoStore::AzuAutoStore.Util.Boxes::AddContainer
```

`/hoard` in chat prints the same list with each target's state.

## How the publish works

`tools/publish-hexium.py` uses the standard library only:

1. `POST /api/experimental/usermedia/initiate-upload/` returns an upload UUID and a presigned URL per part.
2. `PUT` each part, keeping the returned `ETag`.
3. `POST /api/experimental/usermedia/{uuid}/finish-upload/` with those ETags.
4. `POST /api/experimental/submission/submit/` with `author_name` set to the team and `communities` set to `["valheim"]`.

The endpoints match Thunderstore's API. For authorization the script tries `Bearer` first and falls back to `Token`, printing which one the server accepted.

It can also be run by hand:

```bash
HEXIUM_TOKEN=... python3 tools/publish-hexium.py dist/Hoard-0.1.0.zip --team isimp --categories "Quality of Life,Mechanics,Open Source"
python3 tools/publish-hexium.py dist/Hoard-0.1.0.zip --check-only   # validate only
```

Valid category slugs are listed at `https://hexium.gg/api/experimental/community/valheim/category/`.

## After a game update

Regenerate the reference stubs, then rebuild and commit `lib/`:

```powershell
pwsh tools/strip-references.ps1
```

The script reads the installed Valheim and BepInEx, replaces every method body with `throw null` and writes the result to `lib/`. The stubs can be compiled against but not run. If you add a reference to `Hoard.csproj`, add it to the list in the script as well.
