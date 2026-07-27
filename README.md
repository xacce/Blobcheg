# Blobcheg

Блоб-БД поверх бинарных файлов для Unity. Данные пекутся в едиторе в один файл на домен, в рантайме
файл лежит в памяти целиком, а чтение — реинтерпретация по оффсету. Burst-совместимо,
`BlobAssetReference` не используется.

Unity 6000.3+, зависимости: Burst, Collections, Mathematics.

## Модель

Одна база = один **домен** = один маркер-интерфейс = один файл. Запись в базе адресуется
**оффсетом**, и только им: таблиц в файле нет. Оффсет хранит потребитель — в `BlobchegRefSo`,
sub-asset'е, который пересборка создаёт на пару (нода × домен) и перевыставляет.

Содержимое записи не проверяется: `Read<T>` реинтерпретирует байты. Проверяется целостность файла
целиком — при подъёме базы, всегда.

## Быстрый старт

Домен, данные и база — в рантайм-сборке:

```csharp
public interface IHotPathCombatData { }

public struct GunData : IHotPathCombatData { public float ammoMax; public int rpm; }

[Blobcheg(typeof(IHotPathCombatData))]
public partial struct CombatDb { }   // ctor, Read<T>, Dispose, FileName дописывает генератор
```

Нода — в editor-сборке (`BlobchegNodeSo` живёт в Editor-only сборке пакета):

```csharp
[CreateAssetMenu(menuName = "Combat/Gun")]
public sealed class GunNodeSo : BlobchegNodeSo
{
    public float ammoMax = 30f;
    public int rpm = 600;

    public override Type[] OutTypes => new[] { typeof(IHotPathCombatData) };

    public override void Write(ref BlobchegNodeWriter w)
        => w.Add(new GunData { ammoMax = ammoMax, rpm = rpm });
}
```

Ссылка на запись в authoring'е — типизированное поле:

```csharp
public sealed class WeaponAuthoring : MonoBehaviour
{
    public BlobchegRef<GunData> gun;   // пикер покажет только записи GunData

    sealed class Baker : Baker<WeaponAuthoring>
    {
        public override void Bake(WeaponAuthoring a)
        {
            DependsOn(a.gun.Asset);
            AddComponent(GetEntity(TransformUsageFlags.None), new WeaponRef { gun = a.gun.Offset });
        }
    }
}
```

Подъём базы — своя система, живёт пару кадров:

```csharp
public partial struct CombatDbBootSystem : ISystem
{
    BlobchegLoad load;
    bool created;

    public void OnCreate(ref SystemState state)
        => load = BlobchegTransport.Default.Read(CombatDb.FileName, Allocator.Persistent);

    public void OnUpdate(ref SystemState state)
    {
        if (!load.Poll()) return;

        state.EntityManager.CreateSingleton(new CombatDb(load.Acquire()));
        created = true;
        state.Enabled = false;
    }

    public void OnDestroy(ref SystemState state)
    {
        if (created) SystemAPI.GetSingleton<CombatDb>().Dispose();
        else load.Dispose();
    }
}
```

Чтение из джобы:

```csharp
ref readonly var gun = ref db.Read<GunData>(weapon.gun);   // чужой домен не скомпилируется
```

## Пересборка

Кнопки Save нет. Домены пересобираются по импорту ноды, при входе в PlayMode и перед билдом; перед
билдом дополнительно требуется идемпотентность.

Раскладка детерминирована: записи идут группами по конечному типу, внутри типа — по GUID ноды,
сырые блоки (`AddBytes`) — в хвост. Отсюда: **правка значений не двигает оффсеты**, двигает их
только появление или удаление ноды. Ref-ассет переписывается, только если изменились оффсет, тип
записи или ревизия ноды, поэтому нетронутые субсцены не перепекаются.

Выход пересборки — `Assets/StreamingAssets/Blobcheg/{Домен}.bcheg` и манифест домена
`Assets/Blobcheg/{Домен}.asset`. И то и другое производно от ассетов; в гит их класть не нужно.

## Ошибки и дефайны

Ошибка бросается, а не возвращается: нет `TryX` и нет «вернём false, вызывающий разберётся». Нет
файла, битый header, не сошлась целостность, две записи одной ноды в домен, обращение к оффсету до
`Flush`, пустой или чужой `BlobchegRef<T>` — исключение.

| Дефайн | Что включает |
|---|---|
| `ENABLE_UNITY_COLLECTIONS_CHECKS` | границы и выравнивание в `Read<T>` |
| `BLOBCHEG_DEBUG` | секцию с типами и именами нод в файле, сверку типа записи в `Read<T>` |

В релизе `Read<T>` — чистый `AsRef`.

## Сборки

| asmdef | что внутри | платформы |
|---|---|---|
| `Blobcheg.Core` | формат, транспорт, писатель | все |
| `Blobcheg.Runtime` | `[Blobcheg]`, `BlobchegBlob`, `BlobchegRefSo`, `BlobchegRef<T>`, генератор | все |
| `Blobcheg.Authoring` | ноды, пересборка, пикер поля | Editor |

## Генератор

Исходник — `Authoring/CodeGen~/`, собранный `Blobcheg.CodeGen.dll` лежит в `Runtime/` с лейблами
`RoslynAnalyzer` и `RunOnlyOnAssembliesWithReference`, поэтому применяется к сборкам, которые
референсят `Blobcheg.Runtime`.

Пересобрать: `dotnet build -c Release` в `Authoring/CodeGen~/`, затем скопировать DLL в `Runtime/`.
`.meta` не трогать — в нём лейблы и GUID.

## Тесты

```
unity test <project> --mode EditMode --filter Blobcheg.Tests
```

Пакет должен быть в `testables` манифеста проекта.
