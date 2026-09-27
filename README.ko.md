# AutomaTable

[English](README.md) | **한국어**

> [!WARNING]
> AutomaTable은 현재 개발 중입니다. ASP.NET Core 환경에서는 검증을 완료했으며 Unity 환경에서는 검증을 진행하고 있습니다. 첫 안정 버전 전까지 API와 패키지 구조가 변경될 수 있습니다.

> **Define tables once. Generate everything else.**

**테이블은 한 번만 정의하세요. 런타임 조회 API부터 Excel 빌드, SQLite 변환, 인덱스와 데이터 검증까지 나머지는 AutomaTable이 자동으로 만듭니다.**

AutomaTable은 Unity 클라이언트와 ASP.NET Core 서버가 같은 C# 테이블 정의, 생성된 조회 API, 데이터 규약을 공유하는 프로젝트를 목표로 설계되었습니다.

## 테이블을 정의하면, 나머지가 따라옵니다

먼저 평범한 C# 모델을 작성합니다. 필요한 조회 조건은 특성으로, 테이블 관계와 에셋 참조는 타입으로 선언합니다.

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

이 타입들은 곧 검증 규칙이기도 합니다. Source Generator가 검증 메타데이터를 생성하고 CLI가 다음 검증을 자동으로 실행합니다.

- `Id<ItemData>`가 실제 `ItemData` 행을 가리키는지 확인합니다.
- `AssetAddress`가 가리키는 경로에 실제로 파일이 존재하는지 확인합니다.
- 각 테이블의 `Id`와 `[FindBy]` 키가 유니크한지 검증합니다.

`GameData/*.xlsx`에 대응하는 워크시트를 두고 명령 하나로 빌드와 검증을 실행합니다.

```powershell
dotnet automatable build --validate
```

AutomaTable은 `Generated/table.db`를 빌드하고, Source Generator는 타입이 완전히 지정된 조회 API를 만들어 줍니다.

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

쿼리 문자열도, 매핑 코드도, 별도의 Importer 프로젝트도, 직접 작성하는 검증 코드도, 직접 관리하는 인덱스도 필요 없습니다. 하나의 모델이 전체 파이프라인을 이끕니다.

```text
C# table models ── Source Generator ──> typed runtime API ──┬─ Unity client
        │                                                  └─ ASP.NET Core server
        └── generated schema + Excel ──> Generated/table.db + validation
```

AutomaTable은 게임이나 애플리케이션의 정적 데이터를 코드와 분리해 관리하면서도, 런타임에서는 타입 안전하게 조회할 수 있게 해줍니다. C# 모델이 스키마의 기준이 되고, Source Generator가 조회 API를 만들며, `automatable` CLI가 사용자 코드를 실행하지 않고 Excel 파일을 검증된 SQLite 데이터베이스로 변환합니다.

## 주요 기능

- Unity 클라이언트와 ASP.NET Core 서버에서 테이블 모델과 생성 API를 공유합니다.
- `[TableRow]`가 붙은 C# 클래스에서 테이블과 조회 API를 자동 생성합니다.
- 여러 `.xlsx` 파일을 하나의 SQLite 데이터베이스로 빌드합니다.
- `Id<T>`로 테이블 간 참조를 타입 안전하게 표현합니다.
- `[FindBy]`, `[FindAllBy]`로 단일·복합 인덱스와 조회 메서드를 함께 정의합니다.
- 중복 키, 잘못된 참조, 누락된 에셋, DB 스키마 불일치를 빌드 전에 검증합니다.
- SQLite 직접 조회와 전체 메모리 로드 모드를 같은 API로 지원합니다.
- 스키마 빌드 산출물을 별도 디렉터리에 유지해 반복 실행 시 증분 빌드를 활용합니다.

## 요구 사항

- 런타임 패키지: `netstandard2.1` 또는 `net10.0` 호환 프로젝트
- CLI 도구: .NET 10 SDK
- 데이터 원본: `.xlsx` 형식의 Excel 통합 문서

## 설치

런타임 패키지와 저장소 로컬 도구를 설치합니다.

```powershell
dotnet add package AutomaTable --prerelease

# 저장소에 도구 manifest가 없을 때 한 번만 실행합니다.
dotnet new tool-manifest
dotnet tool install --local AutomaTable.Tool --prerelease
```

`AutomaTable` 패키지에는 런타임과 Source Generator가 함께 포함됩니다. `AutomaTable.Generator`를 별도로 참조할 필요가 없습니다.

## 빠른 시작

### 1. 테이블 모델 정의

`[TableRow]`를 클래스에 추가하고, 모든 데이터 멤버를 `public get; internal set;` 속성으로 선언합니다. 각 테이블에는 자기 자신을 가리키는 `Id<T>` 형식의 `Id` 속성이 반드시 있어야 합니다.

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

Excel 워크시트와 SQLite 테이블은 클래스명 그대로 `ItemData`를 사용합니다. 런타임 접근자와 생성 클래스에서는 읽기 편하도록 끝의 `Data`를 제거해 `db.Item`, `ItemTable`이 됩니다. 클래스명이 `Data`로 끝나지 않으면 두 이름이 같습니다.

### 2. Excel 데이터 작성

프로젝트의 `GameData` 디렉터리에 `.xlsx` 파일을 둡니다. 파일명은 자유롭게 정할 수 있지만 워크시트 이름은 테이블명과 같아야 합니다.

```text
MyGame/
├─ MyGame.csproj
├─ GameData/
│  └─ Items.xlsx       # "ItemData" 워크시트 포함
└─ Resources/
   └─ Items/Icons/Sword.png
```

`ItemData` 워크시트의 첫 행에는 C# 속성명과 동일한 헤더를 작성합니다.

| Id | Name | Category | Price | Icon |
|---:|---|---|---:|---|
| 100 | Wood Sword | Weapon | 120 | Items/Icons/Sword.png |

도구가 요구하는 형식은 이것이 전부입니다. 테이블 클래스 이름과 같은 워크시트를 만들고 첫 행에 C# 속성명을 적으면 됩니다. 스키마가 맞지 않으면 빌드 단계에서 알려줍니다.

### 3. 데이터베이스 빌드와 검증

프로젝트 또는 솔루션 디렉터리에서 실행합니다.

```powershell
dotnet automatable build --validate
```

기본 경로는 다음과 같습니다.

| 용도 | 기본 경로 |
|---|---|
| Excel 입력 | `<project>/GameData` |
| SQLite 출력 | `<project>/Generated/table.db` |
| 에셋 검증 | `<project>/Resources` |
| 증분 스키마 빌드 | `<project>/obj/AutomaTable/SchemaBuild` |

CLI는 현재 위치에서 솔루션과 AutomaTable 프로젝트를 자동으로 찾습니다. 후보가 둘 이상이면 `--project`로 대상 프로젝트를 지정합니다.

```powershell
dotnet automatable build `
  --project .\Game.Core\Game.Core.csproj `
  --input .\Tables `
  --output .\Assets\GameData\table.db `
  --resources .\Assets\Resources `
  --validate
```

### 4. 런타임에서 조회

Source Generator는 모델을 기반으로 `TableDatabase`, 테이블 접근자, finder 메서드를 생성합니다.

```csharp
using AutomaTable.Primitives;
using AutomaTable.Runtime;

using var db = new TableDatabase();
await db.InitializeAsync("Generated/table.db");

var sword = db.Item.FindById(new Id<ItemData>(100));
var namedItem = db.Item.FindByName("Wood Sword");
var weapons = db.Item.FindAllByCategory(ItemCategory.Weapon);
```

`FindById`는 모든 테이블에 자동으로 생성됩니다. 추가 조회 메서드는 모델에 선언한 finder 특성에 따라 이름과 매개변수가 결정됩니다.

| 선언 | 생성되는 메서드 | 반환값 |
|---|---|---|
| `[FindBy(nameof(Name))]` | `FindByName(name)` | 행 하나 또는 `null` |
| `[FindAllBy(nameof(Category))]` | `FindAllByCategory(category)` | 읽기 전용 목록 |
| `[FindBy(nameof(Type), nameof(Level))]` | `FindByTypeAndLevel(type, level)` | 행 하나 또는 `null` |

`FindBy`에는 UNIQUE 인덱스가 생성되므로 데이터가 중복되면 빌드가 실패합니다. `FindAllBy`는 같은 키를 가진 여러 행을 허용합니다.

## 테이블 간 참조

다른 테이블의 ID를 원시 정수 대신 `Id<T>`로 선언하면 잘못된 테이블의 ID를 넘기는 실수를 컴파일 단계에서 방지할 수 있습니다.

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

`automatable validate`는 `RewardItemId`가 실제 `Item` 행을 가리키는지도 확인합니다. 선택적 참조는 `Id<ItemData>?`로 선언할 수 있습니다.

## 로드 모드

기본값은 필요한 행만 SQLite에서 읽는 `Direct` 모드입니다.

```csharp
await db.InitializeAsync("Generated/table.db", TableDatabaseOptions.Direct);
```

반복 조회가 많고 전체 데이터가 메모리에 들어갈 수 있다면 `PreloadAll`을 사용할 수 있습니다. 공개 조회 API는 두 모드에서 동일합니다.

```csharp
await db.InitializeAsync("Generated/table.db", TableDatabaseOptions.PreloadAll);
```

## 검증

DB를 다시 만들지 않고 현재 산출물만 검증할 수 있습니다.

```powershell
dotnet automatable validate
```

현재 검증하는 항목은 다음과 같습니다.

- 테이블 및 열 스키마 일치 여부
- 모든 테이블의 `Id` 중복 여부
- `[FindBy]` 키의 고유성
- `Id<T>` 참조 무결성
- `AssetAddress` 형식과 실제 파일 존재 여부

검증 실패 시 프로세스가 0이 아닌 종료 코드를 반환하므로 CI 단계에서도 같은 명령을 사용할 수 있습니다.

```powershell
dotnet tool restore
dotnet automatable build --validate
```

## CLI 명령

```text
dotnet automatable build [options]
dotnet automatable validate [options]
```

| 옵션 | build | validate | 설명 |
|---|:---:|:---:|---|
| `--project <path>` | O | O | 대상 `.csproj`를 명시합니다. |
| `--input <path>` | O | - | `.xlsx` 입력 디렉터리를 변경합니다. |
| `--output <path>` | O | O | SQLite DB 경로를 변경합니다. |
| `--resources <path>` | O | O | `AssetAddress` 검증 기준 디렉터리를 변경합니다. |
| `--validate` | O | - | 빌드 직후 검증을 실행합니다. |

도움말은 `dotnet automatable --help`로 확인할 수 있습니다.

## 증분 스키마 빌드

CLI는 사용자 프로젝트를 `AutomaTableEmitSchema=true`로 빌드해 스키마 메타데이터를 읽습니다. 이 메타데이터는 데이터 도구 전용 assembly에만 포함되며 일반 애플리케이션 빌드 결과에는 들어가지 않습니다.

도구 전용 restore 및 build 산출물은 아래 명령과 같은 구조로 `<project>/obj/AutomaTable/SchemaBuild`에 분리됩니다.

```powershell
dotnet build MyGame.csproj `
  --configuration Release `
  --artifacts-path .\obj\AutomaTable\SchemaBuild `
  -p:AutomaTableEmitSchema=true `
  --no-restore
```

패키지 참조, 대상 프레임워크 등의 restore 입력 변경을 안전하게 감지할 수 있도록 스키마 빌드 전에 매번 해당 산출물 디렉터리를 restore합니다. NuGet restore 자체는 증분 방식으로 동작하며, 이어지는 빌드도 변경되지 않은 프로젝트의 기존 MSBuild 결과를 재사용합니다. 따라서 `dotnet automatable build`를 실행할 때마다 DLL을 처음부터 다시 만들지 않습니다.

## 지원하는 멤버 타입

| 분류 | C# 타입 |
|---|---|
| 정수 | `byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong` |
| 실수 | `float`, `double`, `decimal` |
| 기타 기본 타입 | `bool`, `string`, `string?`, enum |
| 시간과 식별자 | `DateTime`, `DateTimeOffset`, `TimeSpan`, `Guid` |
| AutomaTable 타입 | `Id<T>`, `Id<T>?`, `AssetAddress`, `AssetAddress?` |
| 바이너리 | `ReadOnlyMemory<byte>`, `ReadOnlyMemory<byte>?` (Excel에서는 Base64 문자열) |

지원하지 않는 타입이나 잘못된 모델 선언은 `TABLE001`부터 `TABLE006`까지의 컴파일 진단으로 보고됩니다.

## 이 저장소에서 직접 실행하기

패키징 전 소스를 테스트할 때는 저장소 루트에서 전체 테스트를 실행합니다.

```powershell
dotnet test
```

CLI를 로컬 프로젝트로 직접 실행하려면 `AutomaTable.Tests` 디렉터리에서 다음 명령을 사용할 수 있습니다.

```powershell
dotnet run --project ..\AutomaTable.Tool\AutomaTable.Tool.csproj -- build --validate
```

이 명령은 테스트 프로젝트의 `GameData`를 읽어 `AutomaTable.Tests/Generated/table.db`를 만들며, `Generated/`는 테스트 프로젝트의 `.gitignore`에서 제외됩니다.

## 저장소 구성

```text
AutomaTable/
├─ AutomaTable/             Runtime, annotations, NuGet packaging
├─ AutomaTable.Generator/   Runtime API and conditional schema generation
├─ AutomaTable.Tool/        Project discovery, Excel import, SQLite build, validation
└─ AutomaTable.Tests/       Models, sample workbooks, pipeline tests
```

외부 사용자가 설치하는 단위는 런타임 패키지 `AutomaTable`과 로컬 CLI 도구 `AutomaTable.Tool` 두 개입니다. Generator는 런타임 패키지에 포함되고, 데이터 변환 구현은 CLI 내부에 숨겨집니다.

## 로드맵

- [x] C# 테이블 모델 기반 런타임 조회 API 생성
- [x] 여러 `.xlsx` 파일을 하나의 SQLite 데이터베이스로 빌드
- [x] `Id<T>` 기반의 타입 안전한 테이블 참조
- [x] `[FindBy]`, `[FindAllBy]` 기반 finder와 SQLite 인덱스 생성
- [x] 스키마, 고유 키, 참조 무결성, `AssetAddress` 검증
- [x] SQLite 직접 조회와 전체 데이터 preload 모드
- [x] ASP.NET Core 환경에서 패키지와 생성된 런타임 API 검증
- [ ] Unity 환경에서 패키지와 생성된 런타임 API 검증 (진행 중)
- [x] CLI 전용 schema manifest v1 생성
- [x] 격리된 산출물 디렉터리를 사용하는 증분 스키마 빌드
- [ ] 빌드 결과를 비교할 수 있는 JSON diff 출력
- [ ] 문자열 ID와 IdMap 지원
- [ ] 사용자 정의 타입 converter
- [ ] 선택적으로 사용할 수 있는 NUnit 검증 adapter
