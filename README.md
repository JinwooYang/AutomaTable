# AutomaTable

> Define tables once. Generate everything else.

게임 데이터 테이블을 한 번 정의하면 **클라이언트/서버에서 공통으로 사용할 테이블 코드**, **Excel 데이터를 런타임 DB로 변환하는 Importer**, **데이터 무결성을 검증하는 테스트 코드**까지 자동으로 생성하는 C# 데이터 테이블 자동화 도구입니다.

반복적으로 작성해야 하는 테이블 조회 코드, Excel 파싱 코드, 데이터 검증 코드를 Source Generator를 통해 자동화하고, 하나의 테이블 정의를 데이터 파이프라인 전체의 기준으로 사용하는 것을 목표로 합니다.

## Overview

게임에서 데이터 테이블 하나를 추가하려면 실제로는 데이터 클래스만 작성하는 것으로 끝나지 않는 경우가 많습니다.

* 클라이언트와 서버에서 사용할 조회 코드
* Excel 데이터를 런타임 데이터로 변환하는 Importer
* Unique Key / Reference 등의 데이터 무결성 검증
* 런타임에서 사용할 DB 및 인덱스 구성

AutomaTable은 테이블 정의를 기준으로 이러한 반복 작업을 자동으로 생성합니다.

```text
                       ┌─ Runtime Table Code
                       │   └─ Client / Server
                       │
Table Definition ──────┼─ Excel Importer
                       │   └─ XLSX → SQLite DB
                       │
                       └─ Validation Tests
                           └─ NUnit
```

테이블 구조나 조회 조건이 변경되면 관련 코드 역시 Source Generator를 통해 함께 갱신됩니다.

---

## 데이터 정의

테이블은 일반 C# 클래스로 정의합니다.

```csharp
using AutomaTable.Annotations;
using AutomaTable.Models.Items;
using AutomaTable.Primitives;

namespace AutomaTable.Models.Quests;

[TableRow]
[FindAllBy(nameof(Type), nameof(RepeatType))]
public sealed class QuestData
{
    public Id<QuestData> Id { get; internal set; }

    public QuestType Type { get; internal set; }
    public QuestRepeatType RepeatType { get; internal set; }

    public AssetAddress IconAddress { get; internal set; }

    public Id<ItemData> RewardItemId { get; internal set; }
}
```

`[TableRow]`가 선언된 타입을 기준으로 런타임 테이블, Importer, 데이터 검증 테스트가 생성됩니다.

조회 조건 역시 테이블 정의에 함께 선언합니다.

```csharp
[FindBy(nameof(Name))]
[FindAllBy(nameof(Type))]
public sealed class ItemData
{
    // ...
}
```

`FindBy`는 하나의 행을 찾는 조회를, `FindAllBy`는 동일한 키를 가진 여러 행을 조회하는 API를 생성합니다.

---

## Runtime Table

`AutomaTable.Generator`는 `[TableRow]` 모델을 분석해 테이블별 조회 코드를 생성합니다.

예를 들어 위의 `QuestData` 정의로부터 다음과 같은 API를 사용할 수 있습니다.

```csharp
using var db = new TableDatabase();

await db.InitializeAsync("table.db");

var quest = db.Quest.FindById(new Id<QuestData>(1));

var dailySubQuests = db.Quest.FindAllByTypeAndRepeatType(
    QuestType.Sub,
    QuestRepeatType.Daily);
```

테이블 코드는 런타임 환경에 따라 두 가지 방식으로 사용할 수 있습니다.

### Direct

기본 모드에서는 조회 시 SQLite DB를 직접 조회합니다.

```csharp
await db.InitializeAsync(
    "table.db",
    TableDatabaseOptions.Direct);
```

클라이언트처럼 전체 테이블을 메모리에 올리지 않고 필요한 데이터를 조회하는 환경을 위한 방식입니다.

### PreloadAll

```csharp
await db.InitializeAsync(
    "table.db",
    TableDatabaseOptions.PreloadAll);
```

모든 데이터를 초기화 시점에 읽어 메모리 인덱스를 생성합니다.

서버처럼 테이블 전체를 메모리에 유지하면서 빠르게 조회하는 환경을 위한 방식입니다.

AutomaTable은 동일한 테이블 정의와 조회 API를 서로 다른 런타임 환경에서 공유하는 것을 목표로 합니다.

---

## Excel Importer

`AutomaTable.Importer`는 Excel 파일을 읽어 런타임에서 사용할 SQLite DB를 생성합니다.

```text
XLSX
 │
 ▼
AutomaTable.Importer
 │
 ▼
table.db
```

Importer에서 필요한 테이블 생성 코드, Excel 셀 변환 코드, SQLite 바인딩 코드와 인덱스 생성 코드 역시 테이블 정의를 기준으로 자동 생성됩니다.

따라서 새로운 테이블이 추가되더라도 별도의 Excel 파싱 코드를 직접 구현할 필요가 없습니다.

기본적으로 Worksheet 이름과 `[TableRow]` 클래스 이름을 매칭합니다.

예를 들어:

```text
ItemData.xlsx
└─ ItemData

QuestData.xlsx
└─ QuestData
```

또는 하나의 Workbook 안에 여러 테이블을 구성할 수도 있습니다.

```text
GameData.xlsx
├─ ItemData
├─ QuestData
└─ SkillData
```

Importer 실행 예:

```powershell
dotnet run --project .\AutomaTable.Importer\AutomaTable.Importer.csproj -- `
  .\AutomaTable.Importer\Input `
  .\AutomaTable.Tests\TestData\table.db
```

---

## 데이터 검증

`AutomaTable.Tests.Generator`는 테이블 정의를 분석하여 NUnit 기반 데이터 검증 테스트를 자동 생성합니다.

현재 다음과 같은 데이터 오류를 검증할 수 있습니다.

* `Id` 중복
* `FindBy` 키 중복
* 다른 테이블을 참조하는 `Id<T>`의 유효성
* `AssetAddress`가 가리키는 Resource의 존재 여부

예를 들어:

```csharp
public Id<ItemData> RewardItemId { get; internal set; }
```

와 같이 다른 테이블을 참조하면, 해당 `ItemData`가 실제 데이터에 존재하는지 검증하는 테스트가 생성됩니다.

이를 통해 잘못된 데이터가 실제 클라이언트나 서버에 배포되기 전에 테스트 단계에서 발견하는 것을 목표로 합니다.

---

## Project Structure

```text
AutomaTable
├─ AutomaTable
│  └─ Runtime / Table Definition
│
├─ AutomaTable.Generator
│  └─ Runtime table code generator
│
├─ AutomaTable.Importer
│  └─ XLSX → SQLite DB
│
├─ AutomaTable.Importer.Generator
│  └─ Importer code generator
│
├─ AutomaTable.Tests
│  └─ Generated validation tests
│
└─ AutomaTable.Tests.Generator
   └─ Validation test generator
```

---

## 목표

AutomaTable의 기본 아이디어는 간단합니다.

> **Define tables once. Generate everything else.**

새로운 데이터 테이블을 추가할 때 개발자가 반복적으로 작성해야 하는 코드를 최소화하고,

```text
Table Definition
```

하나를 기준으로

```text
Runtime Code
Importer
Validation
```

가 함께 유지되도록 만드는 것이 목표입니다.

특히 클라이언트와 서버가 동일한 테이블 모델과 조회 규칙을 공유함으로써 양쪽 구현이 서로 달라지는 문제를 줄이는 것을 지향합니다.

---

## Roadmap

현재 프로젝트는 개발 및 검증 단계에 있으며 다음 기능을 추가할 예정입니다.

### Unity / ASP.NET Core Integration

실제 Unity 클라이언트와 ASP.NET Core 서버 프로젝트에 라이브러리를 적용하여 통합 테스트를 진행할 예정입니다.

동일한 테이블 정의와 생성된 코드를 클라이언트와 서버에서 공유하는 구조를 실제 환경에서 검증하는 것이 목표입니다.

### JSON Output

Importer가 SQLite DB를 생성할 때 동일한 데이터를 JSON으로도 출력하도록 개선할 예정입니다.

```text
             ┌─ table.db
Excel ───────┤
             └─ table.json
```

SQLite DB는 런타임에서 사용하고, JSON 파일은 Git에 함께 저장하여 데이터 변경 내용을 diff로 확인하는 용도로 사용합니다.

Excel이나 SQLite 파일은 Git diff만으로 실제 데이터 변경 내용을 파악하기 어렵기 때문에, 사람이 리뷰할 수 있는 텍스트 형태의 데이터를 함께 생성하는 것이 목적입니다.

### String-based ID

현재 Excel에서 사용하는 raw integer ID 대신 사람이 읽기 쉬운 문자열 ID를 입력할 수 있도록 개선할 예정입니다.

예:

```text
wood_sword
iron_sword
legendary_sword
```

Importer가 별도의 `IdMap`을 관리하여 문자열 ID와 실제 런타임에서 사용하는 raw ID를 연결합니다.

```text
wood_sword      <-> 101
iron_sword      <-> 102
legendary_sword <-> 103
```

한번 할당된 raw ID는 유지하여 DB나 사용자 데이터 등 외부에서 해당 ID를 사용하고 있더라도 안정적으로 참조할 수 있도록 하는 것이 목표입니다.

이를 통해 Excel을 작성하는 사람은 숫자 ID를 직접 관리하지 않고 의미 있는 문자열을 사용할 수 있고, 런타임에서는 기존처럼 효율적인 integer ID를 사용할 수 있습니다.

---

## Status

현재 개발 중인 프로젝트입니다.

API와 생성 코드 구조는 Unity / ASP.NET Core 실제 프로젝트 연동 및 데이터 파이프라인 검증 과정에서 변경될 수 있습니다.
