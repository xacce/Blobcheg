using Unity.Entities;

namespace Blobcheg
{
    /// <summary>
    /// Гейт на кадр: в этом мире подъём положил в синглтон новый буфер — первый раз или взамен
    /// пересобранного. Сущность с этим тегом живёт от прохода <see cref="BlobchegBootGroup"/>, на
    /// котором её завели, до следующего, то есть ровно кадр.
    ///
    /// Нужен тому, у кого от базы есть производное: кеш, таблица, чертёж — всё, что снято с базы
    /// один раз и дальше живёт само. Пересборка меняет числа в файле, буфер в синглтоне подменяется
    /// молча, и производное о правке не узнаёт ниоткуда — правка ноды в редакторе просто не доезжает
    /// до мира.
    ///
    /// <code>
    /// state.RequireForUpdate&lt;BlobchegUpdated&gt;();
    /// </code>
    ///
    /// Гейт говорит «что-то перезапеклось», но не что именно: это спрашивают у <c>Version</c> своего
    /// синглтона — номера сборки того файла, из которого прочитан его буфер.
    ///
    /// Гейт живёт кадр, а не тик. Система в <c>FixedStepSimulationSystemGroup</c> кадр без
    /// фиксированного шага пропускает целиком и сигнала не увидит — ей нужен <c>Version</c>, он не
    /// протухает и не зависит от того, в каком кадре его спросили.
    /// </summary>
    public struct BlobchegUpdated : IComponentData
    {
    }

    /// <summary>
    /// Гасит гейт, зажжённый прошлым кадром. Стоит первой в <see cref="BlobchegBootGroup"/>: то есть
    /// после всех, кто читал гейт прошлым кадром, и до бут-систем, которые зажгут его этим.
    ///
    /// Тег — сущность на кадр, а не включаемый компонент: <c>RequireForUpdate</c> у включаемого
    /// смотрит только на наличие компонента и на бит включённости не глядит, так что гейт из него
    /// будил бы потребителя каждый кадр и не значил бы ничего.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]
    [UpdateInGroup(typeof(BlobchegBootGroup), OrderFirst = true)]
    public partial struct BlobchegUpdatedSystem : ISystem
    {
        EntityQuery _gate;

        public void OnCreate(ref SystemState state)
        {
            _gate = state.GetEntityQuery(ComponentType.ReadOnly<BlobchegUpdated>());
            state.RequireForUpdate(_gate);
        }

        public void OnUpdate(ref SystemState state)
        {
            state.EntityManager.DestroyEntity(_gate);
        }
    }

    /// <summary>
    /// Куда бут-система говорит «буфер в синглтоне сменился». Кодогенная зовёт сама; рукописной это
    /// строка в <c>OnCreate</c> и последняя строка подъёма и перезаливки:
    ///
    /// <code>
    /// EntityQuery __gate;
    /// public void OnCreate(ref SystemState state) => __gate = BlobchegBoot.Gate(ref state);
    /// // ... подняли или перечитали:
    /// BlobchegBoot.Updated(ref state, __gate);
    /// </code>
    /// </summary>
    public static class BlobchegBoot
    {
        /// <summary>
        /// Запрос гейта. Спрашивается в <c>OnCreate</c> и держится полем: запрос, собранный в
        /// <c>OnUpdate</c>, Entities встречает варнингом, а пересборка — это как раз <c>OnUpdate</c>.
        /// </summary>
        public static EntityQuery Gate(ref SystemState state)
            => state.GetEntityQuery(ComponentType.ReadOnly<BlobchegUpdated>());

        /// <summary>
        /// Зажигает гейт этого мира. Повторный зов за кадр ничего не меняет: базы перечитываются
        /// каждая своей системой, а гейт у мира один — он про «что-то», а не про «вот это».
        ///
        /// Мира без <see cref="BlobchegUpdatedSystem"/> не бывает в игре, но бывает в тесте,
        /// собравшем мир из одной бут-системы: гасить гейт там некому, и он остаётся зажжённым
        /// навсегда. Это не отказ — в таком мире его никто и не спрашивает.
        /// </summary>
        public static void Updated(ref SystemState state, EntityQuery gate)
        {
            if (gate.IsEmptyIgnoreFilter)
                state.EntityManager.CreateSingleton<BlobchegUpdated>("Blobcheg Updated");
        }
    }
}
