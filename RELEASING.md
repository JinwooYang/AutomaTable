# Release process

NuGet and UPM use the same version. Before preparing a release, update these
three files together:

- `AutomaTable/AutomaTable.csproj`
- `AutomaTable.Tool/AutomaTable.Tool.csproj`
- `AutomaTable.Unity/Packages/com.jinwooyang.automatable/package.json`

Then run:

```powershell
.\eng\prepare-release.ps1
```

The script requires PowerShell 7, the .NET 10 SDK, and npm. Keep the versions
and licenses in the Unity package's `THIRD-PARTY-NOTICES.md` in sync when
updating SQLite dependencies.

This command restores and tests the .NET solution, creates NuGet packages and
symbol packages under `artifacts/packages`, refreshes the managed and native
binaries in the embedded Unity package, and creates a UPM tarball under
`artifacts/upm`.

Before publishing, rebuild the Unity project's database as described in
`AutomaTable.Unity/README.md`, open the project in the Unity version named in
`ProjectSettings/ProjectVersion.txt`, and run its EditMode and PlayMode tests.
For Android releases, build an ARM64 IL2CPP player and run the PlayMode test on
a device. Check that the APK contains both `assets/AutomaTable/table.db` and
`lib/arm64-v8a/libe_sqlite3.so`.

Always run the Android build from the release commit. A successful player test
from a different project location or an earlier commit is not a substitute.

Inspect the generated packages before publishing. Publishing is intentionally a
separate manual step:

```powershell
$releaseVersion = '0.1.0-alpha.2' # Replace with the version being released.

dotnet nuget push ".\artifacts\packages\AutomaTable.$releaseVersion.nupkg" `
  --source https://api.nuget.org/v3/index.json `
  --api-key $env:NUGET_API_KEY

dotnet nuget push ".\artifacts\packages\AutomaTable.Tool.$releaseVersion.nupkg" `
  --source https://api.nuget.org/v3/index.json `
  --api-key $env:NUGET_API_KEY
```

Keep each matching `.snupkg` next to its `.nupkg`; `dotnet nuget push` uploads
the symbol package with the main package unless `--no-symbols` is specified.

For Git-based UPM installation, commit the prepared package and tag that commit
with a release tag such as `v<version>`. Consumers can then use:

```text
https://github.com/JinwooYang/AutomaTable.git?path=/AutomaTable.Unity/Packages/com.jinwooyang.automatable#v<version>
```

Do not create or move a release tag until both NuGet packages and the UPM
package contents have been verified from the tagged commit.
