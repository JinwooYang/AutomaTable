# AutomaTable for Unity

Define table models once in C#. AutomaTable generates type-safe query APIs and
turns Excel workbooks into a validated SQLite database shared with an ASP.NET
Core server.

This Unity 6 package includes the `netstandard2.1` runtime, the Roslyn source
generator, managed SQLite dependencies, and native SQLite libraries for Windows
x64 and Android ARM64. The generator is imported with Unity's `RoslynAnalyzer`
label and runs when Unity compiles your table models.

## Install

Add this Git URL in Unity Package Manager (`+` → **Install package from git URL**):

```text
https://github.com/JinwooYang/AutomaTable.git?path=/AutomaTable.Unity/Packages/com.jinwooyang.automatable#<release-tag>
```

For local development in this repository, open `AutomaTable.Unity` directly;
the package is embedded under `Packages/`.

Create a separate .NET schema project that compiles the same C# table models as
Unity, then use the `AutomaTable.Tool` .NET tool to build and validate the Excel
data. The [repository test project](https://github.com/JinwooYang/AutomaTable/tree/main/AutomaTable.Unity)
shows the complete setup.

## Open the database

Place the generated `table.db` in `Assets/StreamingAssets/AutomaTable/`. Before
opening it, resolve it to a local file path:

```csharp
using AutomaTable.Runtime;
using AutomaTable.Unity;

var databasePath = await AutomaTableDatabaseFile.PrepareAsync(
    "AutomaTable/table.db");

AutomaTableUnityBootstrap.EnsureInitialized();
using var database = new TableDatabase();
await database.InitializeAsync(databasePath);
```

In the Editor and on Windows desktop, `PrepareAsync` returns the file in
`StreamingAssets`. On Android, it copies the file from inside the APK to
`Application.persistentDataPath` so SQLite can open it.

## Supported and tested

- Unity Editor on Windows x64: EditMode and PlayMode tests pass.
- Android ARM64 with IL2CPP: APK builds and the PlayMode test has passed on a
  device.

Other native platforms have not been packaged yet. See the package
[changelog](CHANGELOG.md), [license](LICENSE.md), and
[third-party notices](THIRD-PARTY-NOTICES.md).
