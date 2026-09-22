# DataTable

SQLite 테이블 행을 C# 타입으로 선언하면 조회 API를 컴파일 시점에 생성하는 라이브러리입니다. `DataTable`은 `netstandard2.1`과 `net10.0`을 동시에 대상으로 하며, SQLite 계층으로 `sqlite-net-pcl`을 사용합니다.

## 데이터 선언

```csharp
using DataTable.Annotations;
using DataTable.Models.Items;
using DataTable.Primitives;

namespace DataTable.Models.Quests;

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

모든 `[TableRow]`는 자기 타입을 가리키는 `Id` 프로퍼티를 반드시 직접 선언해야 합니다. 생성기는 이 규칙을 만족하지 않는 모델에 `TABLE006` 오류를 보고하고, 올바른 `Id`로부터 `FindById()`를 항상 생성합니다. 따라서 `Id`에는 `[FindBy]`를 따로 선언하지 않습니다.

`FindBy`와 `FindAllBy`에는 `Id` 이외의 한 개 이상의 키 이름을 전달할 수 있습니다. 생성된 조회 함수는 호출할 때마다 SQLite를 직접 조회하며 애플리케이션 레벨 캐시를 사용하지 않습니다.

테이블 행은 외부 코드에서 변경할 수 없도록 `public get`과 `internal set` 프로퍼티로 정의해야 합니다. public 필드나 public setter를 사용하면 생성기가 `TABLE004` 오류를 보고합니다. internal setter는 같은 어셈블리의 생성 코드가 SQLite 값을 초기화할 때만 사용됩니다.

DBImporter는 각 테이블의 `Id`를 `INTEGER PRIMARY KEY`로 생성해야 합니다. `Id`용 별도 인덱스는 만들지 않습니다. 그 밖의 조회 정의에 맞는 단일 또는 복합 SQLite 인덱스는 `IX_{행 타입 이름}_{키 이름들}` 규칙으로 생성합니다. `FindBy` 인덱스는 `UNIQUE`여야 하고 `FindAllBy` 인덱스는 일반 인덱스를 사용할 수 있습니다. 예를 들어 위 복합 조회에는 `IX_QuestData_Type_RepeatType`이 필요합니다.

## 사용

```csharp
using DataTable.Models.Quests;
using DataTable.Primitives;
using DataTable.Runtime;

using var db = new TableDatabase();
await db.InitializeAsync("table.db");

var quest = db.Quest.FindById(new Id<QuestData>(1));
if (quest != null)
{
    var item = db.Item.FindById(quest.RewardItemId);
}

var dailySubQuests = db.Quest.FindAllByTypeAndRepeatType(
    QuestType.Sub,
    QuestRepeatType.Daily);
```

서버처럼 모든 데이터를 메모리에 올릴 환경에서는 초기화 옵션을 명시합니다.

```csharp
using DataTable.Runtime;

using var db = new TableDatabase();
await db.InitializeAsync("table.db", TableDatabaseOptions.PreloadAll);
```

`PreloadAll`은 모든 행을 한 번만 읽고 `FindBy`와 `FindAllBy`별 메모리 인덱스를 구성합니다. 초기화가 끝나면 SQLite connection을 닫으며, 이후 조회는 읽기 전용 메모리 인덱스만 사용합니다. 옵션을 생략하면 기존처럼 `TableDatabaseOptions.Direct`가 적용되어 매번 SQLite를 조회합니다.

`InitializeAsync`는 기존 DB 파일을 읽기 전용으로 열고 필수 테이블과 컬럼, `Id INTEGER PRIMARY KEY`, 조회 인덱스와 인덱스 컬럼 순서 및 `FindBy` 인덱스의 UNIQUE 여부를 검증합니다. 파일이나 필수 스키마가 없으면 초기화가 실패하며, 테이블이나 인덱스를 생성·변경하지 않습니다. 테이블 이름은 행 클래스 이름(`QuestData`)이며 컬럼 이름은 공개 프로퍼티 이름입니다. 데이터와 스키마는 추후 DBImporter 같은 별도 도구에서 만들어야 합니다.

조회 코드는 sqlite-net ORM 행 객체를 만들지 않습니다. 생성된 코드는 SQLite statement의 컬럼을 최종 `ItemData`/`QuestData` 객체에 직접 읽으므로 별도의 `StorageRow`, 변환용 `ToModel`, 중간 행 목록이 생성되지 않습니다.

생성 결과는 `DataTable.Runtime` 네임스페이스 아래의 테이블별 파일(`DataTable.Runtime.ItemTable.g.cs`, `DataTable.Runtime.QuestTable.g.cs`)과 전역 조립 파일(`DataTable.Runtime.TableDatabase.g.cs`)로 분리됩니다. 프로퍼티나 조회 정의가 바뀌면 해당 테이블 출력만 다시 생성되고, `TableDatabase` 출력은 테이블의 추가·삭제·이름 변경처럼 테이블 목록이 달라질 때만 갱신됩니다.

현재 기본 지원 타입은 `Id<T>`, `Id<T>?`, enum, `AssetAddress`, `AssetAddress?`, C# 숫자/논리/문자열 스칼라, `decimal`, `DateTime`, `DateTimeOffset`, `TimeSpan`, `Guid`, `ReadOnlyMemory<byte>`와 `ReadOnlyMemory<byte>?`입니다. `ReadOnlyMemory<byte>`는 SQLite BLOB 컬럼과 매핑하며 일반 API에서 내용을 직접 변경할 수 없는 읽기 전용 뷰로 노출됩니다. BLOB 프로퍼티는 `FindBy`나 `FindAllBy` 키로 사용할 수 없으며, 사용하면 `TABLE005` 컴파일 오류가 발생합니다. 자기 타입의 `Id`가 없거나 선언 형태가 잘못되면 `TABLE006` 오류가 발생합니다. 그 밖의 지원하지 않는 타입이나 존재하지 않는 조회 키는 `TABLE001`/`TABLE002` 오류로 보고됩니다.

## 데이터 검증 테스트

`DataTable.Tests.Generator`는 참조된 `[TableRow]` 모델을 분석하여 NUnit 테스트를 테이블별 파일로 생성합니다. 생성된 테스트는 `Id`와 `FindBy` 키 조합의 유일성, 다른 테이블을 참조하는 `Id<T>` 값, `AssetAddress`가 가리키는 Resources 파일을 검증합니다. `FindAllBy`는 여러 행 반환이 목적이므로 유일성 검사 대상이 아닙니다.

테스트 DB의 기본 위치는 `DataTable.Tests/TestData/table.db`, 리소스의 기본 위치는 `DataTable.Tests/Resources`입니다. CI에서는 `DATATABLE_TEST_DB_PATH`와 `DATATABLE_TEST_RESOURCES_PATH` 환경 변수로 경로를 변경할 수 있습니다.

`AssetAddress`는 Resources 폴더 기준의 상대 경로이며 파일 확장자를 반드시 포함합니다. 예를 들어 다음 값은 `Resources/Quest/Icons/Main.txt` 파일을 가리킵니다.

```csharp
new AssetAddress("Quest/Icons/Main.txt")
```

`AssetAddress?`와 다른 테이블을 가리키는 `Id<T>?`는 null을 허용합니다. nullable이 아닌 주소나 참조 ID는 null일 수 없으며, 값이 있으면 실제 파일이나 대상 행이 반드시 존재해야 합니다.

## XLSX에서 SQLite DB 생성

`DataTable.Importer`는 지정한 디렉터리 아래의 모든 `.xlsx` 파일을 재귀적으로 검색해 SQLite DB를 생성하는 콘솔 도구입니다. 파일명은 매핑에 사용하지 않으며, `[TableRow]` 클래스 이름과 워크시트 이름을 정확히 매칭합니다. 예를 들어 `QuestData` 테이블에는 어느 워크북에 있든 이름이 `QuestData`인 시트가 필요합니다.

```powershell
dotnet run --project .\DataTable.Importer\DataTable.Importer.csproj -- `
  .\DataTable.Importer\Input `
  .\DataTable.Tests\TestData\table.db
```

현재 샘플 입력은 테이블별 파일로 분리되어 있습니다.

- `DataTable.Importer/Input/ItemData.xlsx` 안의 `ItemData` 시트
- `DataTable.Importer/Input/QuestData.xlsx` 안의 `QuestData` 시트

한 워크북에 여러 테이블 시트를 넣거나 여러 워크북으로 나누어도 됩니다. 단, 입력 디렉터리 전체에서 같은 이름의 시트가 두 번 발견되면 어느 파일을 사용할지 임의로 고르지 않고 import 시작 전에 오류로 종료합니다. 필수 테이블 시트가 없거나, 헤더가 누락·중복되거나, 모델에 없는 헤더가 있어도 오류입니다. 첫 번째 행은 프로퍼티 이름과 동일한 헤더이며 그 아래 행부터 데이터입니다.

`DataTable.Importer.Generator`는 참조된 `[TableRow]` 모델을 분석하여 테이블별 `CREATE TABLE`, 셀 변환, prepared statement 바인딩, 인덱스 생성 코드를 컴파일 시점에 생성합니다. XLSX 데이터만 바뀌면 재컴파일할 필요가 없고 모델 스키마가 바뀔 때만 다시 빌드하면 됩니다. 실행 시에는 ExcelDataReader의 행 API로 셀을 읽어 최종 모델이나 중간 row 객체를 만들지 않고 SQLite statement에 직접 바인딩합니다. enum 셀은 이름 또는 정수 값을 사용할 수 있으며 `ReadOnlyMemory<byte>` BLOB 셀은 Base64 문자열을 사용합니다.

DB는 출력 파일과 같은 디렉터리의 임시 파일에 트랜잭션으로 작성됩니다. 모든 테이블 데이터 삽입이 끝난 뒤 `FindBy`용 UNIQUE 인덱스와 `FindAllBy`용 일반 인덱스를 생성하고, 전체 과정이 성공한 경우에만 기존 출력 DB를 교체합니다. 따라서 잘못된 XLSX 때문에 기존 DB가 부분적으로 덮어써지지 않습니다.

## Unity 배포

Unity에는 `netstandard2.1` 출력과 함께 `sqlite-net-pcl` 및 해당 SQLite 네이티브 런타임 의존성을 가져와야 합니다. 플랫폼별 네이티브 플러그인이 패키징되는지 Editor와 IL2CPP 플레이어 빌드 모두에서 확인해야 합니다. ASP.NET Core에서는 일반 `ProjectReference` 또는 패키지 참조로 사용할 수 있습니다.
