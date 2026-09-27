# AutomaTable Unity dogfooding project

This Unity project verifies that AutomaTable's source generator, SQLite runtime,
validation data, and generated table APIs work together in the Unity Editor and
an Android IL2CPP player.

## Requirements

- Unity `6000.6.3f1`
- .NET 10 SDK
- Android Build Support, SDK, NDK, and OpenJDK for Android builds

## Rebuild the table database

From this directory:

```powershell
dotnet run --project ..\AutomaTable.Tool\AutomaTable.Tool.csproj -- `
  build `
  --project .\Schema\AutomaTable.Unity.Schema.csproj `
  --input .\GameData `
  --output .\Assets\StreamingAssets\AutomaTable\table.db `
  --validate `
  --resources .\Assets\Resources
```

The schema project references the runtime and generator projects in this
repository, so this command does not require a published AutomaTable NuGet
package.

## Run the tests

Open the project in Unity and run:

- EditMode: `TableDatabaseTests.GeneratedRuntimeApiReadsBuiltDatabase`
- PlayMode: `TableDatabasePlayerTests.GeneratedRuntimeApiReadsBuiltDatabaseOnPlayer`

For Android, connect an ARM64 device with USB debugging enabled and run the
PlayMode test on the Android Player from Unity Test Runner.
