# Blobcheg

Блоб-БД поверх бинарных файлов, резидентная в памяти всю сессию. Своя аллокация, чтение —
реинтерпретация по оффсету; `BlobAssetReference` и Unity-блобы не используются.

Спека: `docs/superpowers/specs/2026-07-27-blobcheg-design.md`.

## Правило одно

**Адрес записи — только `offset`.** Ни таблиц в файле, ни индекса, ни хешей имён, ни маппинга
«что-то → offset». Хранить адрес обязан потребитель, и единственный способ это сделать —
`BlobchegRefSo`: sub-asset на пару (нода × домен), который пересборка создаёт и перевыставляет.

Что лежит внутри записи — вопрос доверия. Проверяется только целостность файла целиком (всегда, на
подъёме) и границы (за `ENABLE_UNITY_COLLECTIONS_CHECKS`).

## Сборки

| asmdef | что внутри | платформы |
|---|---|---|
| `Blobcheg.Core` | формат, транспорт, писатель | все |
| `Blobcheg.Runtime` | `[Blobcheg]`, `BlobchegBlob`, `BlobchegRefSo`, `BlobchegRef<T>` + `Blobcheg.CodeGen.dll` | все |
| `Blobcheg.Authoring` | ноды, пересборка, драйвер поля, исходник генератора | **Editor** |

`BlobchegNodeSo` живёт в Editor-only сборке: ноды потребителя обязаны лежать в его editor-сборке,
а структуры данных — в рантайм-сборке.

## Потребителю

```csharp
// рантайм-сборка
public interface IHotPathCombatData { }
public struct GunData : IHotPathCombatData { public float ammoMax; }

[Blobcheg(typeof(IHotPathCombatData))]
public partial struct CombatDb { }                  // тело дописывает генератор

public sealed class WeaponAuthoring : MonoBehaviour
{
    public BlobchegRef<GunData> gun;                // щит сюда не присвоить, тип держит компилятор
}

// editor-сборка
public sealed class GunNodeSo : BlobchegNodeSo
{
    public float ammoMax = 30f;

    public override Type[] OutTypes => new[] { typeof(IHotPathCombatData) };
    public override void Write(ref BlobchegNodeWriter w) => w.Add(new GunData { ammoMax = ammoMax });
}
```

Бут: `BlobchegTransport.Default.Read(CombatDb.FileName, Allocator.Persistent)` в `OnCreate`,
`load.Poll()` в `OnUpdate`, `new CombatDb(load.Acquire())` в синглтон, потребители на
`RequireForUpdate<CombatDb>()`.

## Кнопки Save нет

Пересборка идёт сама: по импорту ноды (`AssetPostprocessor`, отложенно), при входе в PlayMode и
безусловно перед билдом. Раскладка детерминирована, поэтому пересборка идемпотентна — не изменилось
ничего, значит не тронут ни файл, ни один ассет, и ничего не перепекается.

Собранные блобы (`Assets/StreamingAssets/Blobcheg/`) и манифесты доменов (`Assets/Blobcheg/`) —
производные, в гит не идут.

## Генератор

Исходник — `Authoring/CodeGen~/` (папка с `~` в проект не импортируется). Собранный
`Blobcheg.CodeGen.dll` лежит в `Runtime/` с лейблами `RoslynAnalyzer` и
`RunOnlyOnAssembliesWithReference`, чтобы применяться к сборкам, которые референсят
`Blobcheg.Runtime`.

Пересобрать: `dotnet build -c Release` в `Authoring/CodeGen~/`, затем скопировать DLL в `Runtime/`.
`.meta` при этом не трогать — в нём лейблы и GUID.

Генератор выпускает только структуру базы. `ScriptableObject` он выпускать не может: у типа из
кодогена нет `MonoScript`, а `.asset` ссылается именно на него.

## Тесты

`unity test <project> --mode EditMode --filter Blobcheg.Tests`. Пакет подключён в `testables` обоих
манифестов. Полный прогон — 31 тест, включая сквозной путь нода → файл → ref → чтение.
