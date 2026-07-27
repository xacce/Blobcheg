# Blobcheg

Блоб-БД поверх бинарных файлов для Unity. Данные пекутся в едиторе в один файл на домен, в рантайме
файл лежит в памяти целиком, а чтение — реинтерпретация по оффсету. Burst-совместимо,
`BlobAssetReference` не используется.

Unity 6000.3+, зависимости: Burst, Collections, Mathematics. Entities — опционально, только под
бут-группу (сборка `Blobcheg.Entities` гасится сама, если пакета нет).

## Модель

Одна база = один **домен** = один маркер-интерфейс = один файл. Запись в базе адресуется
**оффсетом**, и только им: таблиц в файле нет. Оффсет хранит потребитель — в `BlobchegRefSo`,
sub-asset'е, который пересборка создаёт на пару (нода × домен) и перевыставляет.

Второй адрес — **`BlobchegId`**: имя ноды, общее для всех баз одного роутера. По нему роутер отдаёт
оффсеты ноды во всех своих базах сразу; см. «Роутер».

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

Подъём базы руками — своя система, живёт пару кадров (в проекте на Entities её выпускает кодоген,
см. «Подъём в ECS»):

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

## Роутер: все записи ноды по одному id

Оффсет — прямой путь: знаешь запись на бейке, храни оффсет. Роутер — путь для «на руках только
имя»: один `uint` вместо пачки оффсетов.

```csharp
[BlobchegRouter]
public partial struct GameRouter { }                        // тело дописывает генератор

[Blobcheg(typeof(IHotPathCombatData), "combatData")]        // второй аргумент — имя члена в строке
public partial struct CombatDb { }

[Blobcheg(typeof(IColdData), "coldData")]
public partial struct ColdDb { }
```

Имя члена — это и есть вступление в роутер; без него база живёт сама по себе. Роутер не назван —
берётся единственный роутер **в сборке этой базы**; их ноль или несколько — ошибка компиляции,
`Router = typeof(...)` в атрибуте её снимает. **Роутер и его базы обязаны лежать в одной сборке**:
генератор роутера видит только свою компиляцию. Домен входит максимум в один роутер.

Ссылка в authoring'е — поле, типизированное роутером; бейкер кладёт в компонент `uint`:

```csharp
public BlobchegIdRef<GameRouter> gun;      // пикер покажет только ноды этого роутера
...
AddComponent(entity, new GunRef { id = a.gun.Id });
```

Чтение:

```csharp
var row = router.Get(id);                              // неизвестный id — бросает
ref readonly var hot = ref combatDb.Read<GunData>(row.combatData);   // нет записи — бросает
if (row.HasColdData) { ... }

uint offset = router.GetCombatData(id);                // бросает и на id, и на отсутствии записи
if (router.TryGetCombatData(id, out offset)) { ... }   // не бросает никогда
```

`Get` — метод экземпляра: статикой под Бёрстом он нереализуем (`BC1051`), поэтому роутер живёт
синглтоном ровно как база. Внутри строки — битовая маска и упакованные оффсеты, `flag → index`
считается одним `countbits`.

Нода узнаёт свой id **до записи** — он выводится из `OutTypes`, а не из написанного:

```csharp
public override void Write(ref BlobchegNodeWriter w)
    => w.Add(new GunData { id = w.Id, twin = w.IdOf(twinNode) });
```

`w.Id` — свой id, `w.IdIn<GameRouter>()` — если нода входит сразу в несколько роутеров,
`w.IdOf(node)` — чужой.

Id — позиция строки, а не хеш: правка значений его не двигает, двигают появление и удаление ноды.
Нумерацию бит кодоген и едитор считают независимо, поэтому сходимость доказывается `LayoutHash`:
файл, собранный под другой набор баз, не поднимется.

## Подъём в ECS

Если проект на Entities, подъём писать не нужно: объяви базу или роутер `IComponentData` и сошлись
на сборку `Blobcheg.Entities` — генератор выпустит бут-систему в `BlobchegBootGroup`. Группа стоит в
начале инициализации, **до** `BeginInitializationEntityCommandBufferSystem`.

```csharp
[Blobcheg(typeof(IHotPathCombatData), "combatData")]
public partial struct CombatDb : IComponentData { }   // CombatDbBootSystem выпустит кодоген
```

Не объявил `IComponentData` — подъём пишется руками (см. ниже), группа при этом остаётся к услугам.

## Пересборка

Кнопки Save нет. Домены пересобираются по импорту ноды, при входе в PlayMode и перед билдом; перед
билдом дополнительно требуется идемпотентность.

Раскладка детерминирована: записи идут группами по конечному типу, внутри типа — по GUID ноды,
сырые блоки (`AddBytes`) — в хвост. Отсюда: **правка значений не двигает оффсеты**, двигает их
только появление или удаление ноды. Ref-ассет переписывается, только если изменились оффсет, тип
записи или ревизия ноды, поэтому нетронутые субсцены не перепекаются.

Выход пересборки — `Assets/StreamingAssets/Blobcheg/{Домен}.bcheg` и манифест домена
`Assets/Blobcheg/{Домен}.asset`; роутер кладёт туда же `{Роутер}.bcheg` и свой манифест, где ноды
перечислены в порядке id. Всё это производно от ассетов; в гит класть не нужно.

## Ошибки и дефайны

Ошибка бросается, а не возвращается: нет `TryX` и нет «вернём false, вызывающий разберётся». Нет
файла, битый header, не сошлась целостность, две записи одной ноды в домен, обращение к оффсету до
`Flush`, пустой или чужой `BlobchegRef<T>`/`BlobchegIdRef<T>`, неизвестный id, отсутствие записи в
базе — исключение. Единственное исключение из правила — `TryGet*`/`Has*` у роутера: там отсутствие
записи и есть нормальный ответ, и они не бросают никогда.

| Дефайн | Что включает |
|---|---|
| `ENABLE_UNITY_COLLECTIONS_CHECKS` | границы и выравнивание в `Read<T>` |
| `BLOBCHEG_DEBUG` | секцию с типами и именами нод в файле, сверку типа записи в `Read<T>` |

В релизе `Read<T>` — чистый `AsRef`.

## Сборки

| asmdef | что внутри | платформы |
|---|---|---|
| `Blobcheg.Core` | формат, транспорт, писатель | все |
| `Blobcheg.Runtime` | `[Blobcheg]`, `[BlobchegRouter]`, `BlobchegBlob`, `BlobchegRouterBlob`, `BlobchegId`, ref-поля, генератор | все |
| `Blobcheg.Entities` | `BlobchegBootGroup` | все, только с Entities |
| `Blobcheg.Authoring` | ноды, пересборка, реестр роутеров, пикеры полей | Editor |

## Генератор

Исходник — `Authoring/CodeGen~/`, собранный `Blobcheg.CodeGen.dll` лежит в `Runtime/` с лейблами
`RoslynAnalyzer` и `RunOnlyOnAssembliesWithReference`, поэтому применяется к сборкам, которые
референсят `Blobcheg.Runtime`.

Пересобрать: `dotnet build -c Release` в `Authoring/CodeGen~/`, затем скопировать DLL в `Runtime/`.
`.meta` не трогать — в нём лейблы и GUID.

## Тесты

```
unity test <project> --mode EditMode --filter Blobcheg
```

Фильтр по `Blobcheg`, а не по `Blobcheg.Tests`: тесты бут-группы лежат отдельной сборкой
`Blobcheg.Entities.Tests` (она гасится без Entities). Пакет должен быть в `testables` манифеста
проекта.
