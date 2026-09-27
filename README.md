# AutomaTable

**English** | [한국어](README.ko.md)

> [!WARNING]
> AutomaTable is under active development. The generated runtime API has been validated in ASP.NET Core and in Unity Editor and Android ARM64 player tests. APIs and package structure may change before the first stable release.

> **Define tables once. Generate everything else.**

**Define each table once in C#. AutomaTable generates the runtime query API, builds Excel data into SQLite, creates indexes, and validates the result.**

AutomaTable is designed for projects where a Unity client and an ASP.NET Core server share the same C# table definitions, generated query API, and data contract.

## Define a table. Get the whole pipeline.

Start with ordinary C# models. Attributes declare the queries you want, while types declare relationships and asset references:

```csharp
using AutomaTable.Annotations;
using AutomaTable.Primitives;

public enum ItemCategory { Weapon, Armor, Consumable }

[TableRow]
[FindBy(nameof(Name))]
[FindAllBy(nameof(Category))]
public sealed class ItemData
{
    public Id<ItemData> Id { get; internal set; }
    public string Name { get; internal set; } = null!;
    public ItemCategory Category { get; internal set; }
    public AssetAddress IconAddress { get; internal set; }
}

[TableRow]
public sealed class QuestData
{
    public Id<QuestData> Id { get; internal set; }
    public string Title { get; internal set; } = null!;
    public Id<ItemData> RewardItemId { get; internal set; }
}
```

These types also describe validation rules. The source generator emits the validation metadata, and the CLI runs the corresponding checks automatically:

- `Id<ItemData>` requires the referenced `ItemData` row to exist.
- Verify that a file exists at the path referenced by each `AssetAddress`.
- Verify that every table's `Id` and each `[FindBy]` key are unique.

Put the matching worksheets in `GameData/*.xlsx`, then build and validate them with one command:

```powershell
dotnet automatable build --validate
```

AutomaTable builds `Generated/table.db` and the source generator gives you a fully typed query API:

```csharp
using AutomaTable.Primitives;
using AutomaTable.Runtime;

using var db = new TableDatabase();
await db.InitializeAsync("Generated/table.db");

var item = db.Item.FindById(new Id<ItemData>(100));
var sword = db.Item.FindByName("Wood Sword");
var weapons = db.Item.FindAllByCategory(ItemCategory.Weapon);

var quest = db.Quest.FindById(new Id<QuestData>(1));
var reward = db.Item.FindById(quest!.RewardItemId);
```

No query strings, mapping code, importer project, hand-written validation, or manually maintained indexes. The model drives the entire pipeline:

```text
C# table models ── Source Generator ──> typed runtime API ──┬─ Unity client
        │                                                  └─ ASP.NET Core server
        └── generated schema + Excel ──> Generated/table.db + validation
```

AutomaTable lets you keep static game or application data outside your codebase without giving up type-safe access at runtime. Your C# models remain the source of truth: the source generator creates the query API, while the `automatable` CLI converts Excel workbooks into a validated SQLite database without executing your application code.

## Features

- Share table models and generated APIs between Unity clients and ASP.NET Core servers.
- Generate tables and query APIs from C# classes marked with `[TableRow]`.
- Build multiple `.xlsx` workbooks into a single SQLite database.
- Express type-safe relationships between tables with `Id<T>`.
- Define single-column and composite indexes with `[FindBy]` and `[FindAllBy]`.
- Validate duplicate keys, broken references, missing assets, and database schema mismatches.
- Use the same public API for direct SQLite queries and fully preloaded data.
- Keep schema-build artifacts isolated and reuse them through incremental builds.

## Requirements

- Runtime package: a project compatible with `netstandard2.1` or `net10.0`
- CLI tool: .NET 10 SDK
- Data source: Excel workbooks in `.xlsx` format

## Installation

Install the runtime package and the repository-local tool:

```powershell
dotnet add package AutomaTable --prerelease

# Run this once if the repository does not have a tool manifest yet.
dotnet new tool-manifest
dotnet tool install --local AutomaTable.Tool --prerelease
```

The `AutomaTable` package includes both the runtime and the source generator. You do not need to reference `AutomaTable.Generator` separately.

For Unity 6, the UPM package lives at `AutomaTable.Unity/Packages/com.jinwooyang.automatable` in this repository. Install a tagged release from its Git subfolder:

```text
https://github.com/JinwooYang/AutomaTable.git?path=/AutomaTable.Unity/Packages/com.jinwooyang.automatable#<release-tag>
```

The [Unity test project](AutomaTable.Unity/README.md) includes an embedded copy of that package, sample workbooks, generated SQLite data, and EditMode/PlayMode tests.

## Quick start

### 1. Define a table model

Add `[TableRow]` to a class and declare each data member as a `public get; internal set;` property. Every table must have an `Id` property typed as `Id<T>`, where `T` is the row type itself.

```csharp
using AutomaTable.Annotations;
using AutomaTable.Primitives;

public enum ItemCategory
{
    Weapon,
    Armor,
    Consumable
}

[TableRow]
[FindBy(nameof(Name))]
[FindAllBy(nameof(Category))]
public sealed class ItemData
{
    public Id<ItemData> Id { get; internal set; }
    public string Name { get; internal set; } = null!;
    public ItemCategory Category { get; internal set; }
    public int Price { get; internal set; }
    public AssetAddress Icon { get; internal set; }
}
```

The Excel worksheet and SQLite table use the full class name, `ItemData`. For a class name ending in `Data`, the generated runtime API drops that suffix for readability, producing `db.Item` and `ItemTable`. Names without the `Data` suffix remain unchanged.

### 2. Create the Excel data

Place `.xlsx` files in the project's `GameData` directory. Workbook filenames are unrestricted, but each worksheet name must match its table class name.

```text
MyGame/
├─ MyGame.csproj
├─ GameData/
│  └─ Items.xlsx       # Contains the "ItemData" worksheet
└─ Resources/
   └─ Items/Icons/Sword.png
```

The first row of the `ItemData` worksheet contains headers that exactly match the C# property names:

| Id | Name | Category | Price | Icon |
|---:|---|---|---:|---|
| 100 | Wood Sword | Weapon | 120 | Items/Icons/Sword.png |

That is the complete shape required by the tool: a worksheet named after the table class, with C# property names in its first row. Schema mistakes are reported during the build.

### 3. Build and validate the database

Run the tool from the project or solution directory:

```powershell
dotnet automatable build --validate
```

Default paths:

| Purpose | Default path |
|---|---|
| Excel input | `<project>/GameData` |
| SQLite output | `<project>/Generated/table.db` |
| Asset validation root | `<project>/Resources` |
| Incremental schema build | `<project>/obj/AutomaTable/SchemaBuild` |

The CLI discovers the solution and AutomaTable project from the current directory. If it finds more than one candidate, select one with `--project`:

```powershell
dotnet automatable build `
  --project .\Game.Core\Game.Core.csproj `
  --input .\Tables `
  --output .\Assets\GameData\table.db `
  --resources .\Assets\Resources `
  --validate
```

### 4. Query the data at runtime

The source generator creates `TableDatabase`, table accessors, and finder methods from your models:

```csharp
using AutomaTable.Primitives;
using AutomaTable.Runtime;

using var db = new TableDatabase();
await db.InitializeAsync("Generated/table.db");

var sword = db.Item.FindById(new Id<ItemData>(100));
var namedItem = db.Item.FindByName("Wood Sword");
var weapons = db.Item.FindAllByCategory(ItemCategory.Weapon);
```

Every table receives a `FindById` method. Additional method names and parameters are derived from the finder attributes declared on the model.

| Declaration | Generated method | Return value |
|---|---|---|
| `[FindBy(nameof(Name))]` | `FindByName(name)` | One row or `null` |
| `[FindAllBy(nameof(Category))]` | `FindAllByCategory(category)` | Read-only list |
| `[FindBy(nameof(Type), nameof(Level))]` | `FindByTypeAndLevel(type, level)` | One row or `null` |

`FindBy` creates a UNIQUE index, so duplicate data fails the build. `FindAllBy` allows multiple rows to share the same key.

## References between tables

Use `Id<T>` instead of a raw integer to prevent IDs from unrelated tables from being mixed at compile time:

```csharp
[TableRow]
public sealed class QuestData
{
    public Id<QuestData> Id { get; internal set; }
    public string Title { get; internal set; } = null!;
    public Id<ItemData> RewardItemId { get; internal set; }
}
```

```csharp
var quest = db.Quest.FindById(new Id<QuestData>(1));
var reward = db.Item.FindById(quest!.RewardItemId);
```

`automatable validate` also verifies that each `RewardItemId` points to an existing `ItemData` row. Declare optional references as `Id<ItemData>?`.

## Load modes

`Direct` is the default mode and reads rows from SQLite as they are requested:

```csharp
await db.InitializeAsync("Generated/table.db", TableDatabaseOptions.Direct);
```

Use `PreloadAll` when the complete dataset fits in memory and the application performs frequent lookups. Both modes expose the same query API.

```csharp
await db.InitializeAsync("Generated/table.db", TableDatabaseOptions.PreloadAll);
```

## Validation

Validate the current database without rebuilding it:

```powershell
dotnet automatable validate
```

AutomaTable currently validates:

- Table and column schema compatibility
- `Id` uniqueness for every table
- Uniqueness of `[FindBy]` keys
- Referential integrity for `Id<T>` values
- `AssetAddress` syntax and file existence

Validation failures return a non-zero exit code, so the same command can be used in CI:

```powershell
dotnet tool restore
dotnet automatable build --validate
```

## CLI reference

```text
dotnet automatable build [options]
dotnet automatable validate [options]
```

| Option | build | validate | Description |
|---|:---:|:---:|---|
| `--project <path>` | Yes | Yes | Selects the target `.csproj`. |
| `--input <path>` | Yes | No | Overrides the `.xlsx` input directory. |
| `--output <path>` | Yes | Yes | Overrides the SQLite database path. |
| `--resources <path>` | Yes | Yes | Overrides the root used for `AssetAddress` validation. |
| `--validate` | Yes | No | Runs validation immediately after a successful build. |

Run `dotnet automatable --help` to display command-line help.

## Incremental schema builds

The CLI builds the target project with `AutomaTableEmitSchema=true` and reads the resulting schema metadata. This metadata is emitted only into a tool-specific assembly and is not included in a regular application build.

Restore and build artifacts are isolated under `<project>/obj/AutomaTable/SchemaBuild`, equivalent to the following command:

```powershell
dotnet build MyGame.csproj `
  --configuration Release `
  --artifacts-path .\obj\AutomaTable\SchemaBuild `
  -p:AutomaTableEmitSchema=true `
  --no-restore
```

The CLI runs restore against that artifact directory before every schema build so changes to package references, target frameworks, and other restore inputs are detected safely. NuGet restore is incremental, and the following build reuses MSBuild's existing output for unchanged projects, so `dotnet automatable build` does not rebuild the DLL from scratch every time.

## Supported member types

| Category | C# types |
|---|---|
| Integers | `byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong` |
| Floating point | `float`, `double`, `decimal` |
| Other primitives | `bool`, `string`, `string?`, enum |
| Time and identifiers | `DateTime`, `DateTimeOffset`, `TimeSpan`, `Guid` |
| AutomaTable types | `Id<T>`, `Id<T>?`, `AssetAddress`, `AssetAddress?` |
| Binary | `ReadOnlyMemory<byte>`, `ReadOnlyMemory<byte>?` (Base64 text in Excel) |

Unsupported types and invalid model declarations are reported as compiler diagnostics `TABLE001` through `TABLE006`.

## Working with this repository

Run the complete test suite from the repository root:

```powershell
dotnet test
```

To run the CLI directly from source, use the following command from the `AutomaTable.Tests` directory:

```powershell
dotnet run --project ..\AutomaTable.Tool\AutomaTable.Tool.csproj -- build --validate
```

This reads `GameData` from the test project and creates `AutomaTable.Tests/Generated/table.db`. The test project's `.gitignore` excludes `Generated/`.

## Repository layout

```text
AutomaTable/
├─ AutomaTable/             Runtime, annotations, NuGet packaging
├─ AutomaTable.Generator/   Runtime API and conditional schema generation
├─ AutomaTable.Tool/        Project discovery, Excel import, SQLite build, validation
├─ AutomaTable.Tests/       .NET models, sample workbooks, pipeline tests
├─ AutomaTable.Unity/       Unity Editor and Android dogfooding project, embedded UPM package
└─ eng/                     Local release preparation
```

Consumers install two components: the `AutomaTable` runtime package and the `AutomaTable.Tool` local CLI tool. The runtime package embeds the generator, while the data conversion implementation remains internal to the CLI.

## Roadmap

- [x] Generate a runtime query API from C# table models
- [x] Build multiple `.xlsx` files into a single SQLite database
- [x] Provide type-safe table references through `Id<T>`
- [x] Generate finder methods and SQLite indexes from `[FindBy]` and `[FindAllBy]`
- [x] Validate schemas, unique keys, referential integrity, and `AssetAddress` values
- [x] Support direct SQLite queries and fully preloaded data
- [x] Validate the package and generated runtime API in ASP.NET Core
- [x] Validate the package and generated runtime API in Unity Editor and Android ARM64
- [x] Emit CLI-only schema manifest v1 metadata
- [x] Reuse incremental schema builds in an isolated artifact directory
- [ ] Produce JSON diffs between build results
- [ ] Support string IDs and IdMap
- [ ] Support custom type converters
- [ ] Provide an optional NUnit validation adapter
