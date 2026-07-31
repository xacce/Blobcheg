using System;
using System.Collections.Generic;

namespace Blobcheg.Authoring
{
    /// <summary>
    /// Чужой проход по готовой раскладке. Пересборка находит реализации через <c>TypeCache</c> и
    /// зовёт их после того, как базы сброшены и роутеры собраны, — то есть когда адреса и номера
    /// строк уже существуют, но пересборка ещё не закончилась и её отчёт ещё пишется.
    ///
    /// Заведено затем, чтобы производные файлы (таблица хешей — первый из них) собирались, а ядро
    /// про них не знало: иначе каждый такой файл прорастал бы веткой в <see cref="BlobchegBuild"/>.
    ///
    /// Реализация обязана быть детерминированной: гейт пре-билда гоняет пересборку дважды и требует,
    /// чтобы второй заход не изменил ничего.
    /// </summary>
    public interface IBlobchegBuildPass
    {
        void Run(BlobchegBuildLayout layout, ref BlobchegBuildReport report);
    }

    /// <summary>
    /// Раскладка, какой она получилась: строки роутеров по номерам и адрес каждой записи. Всё,
    /// что нужно производному файлу, и ничего, чем он мог бы раскладку испортить.
    /// </summary>
    public readonly struct BlobchegBuildLayout
    {
        readonly BlobchegIdTable _ids;
        readonly Dictionary<(BlobchegNodeSo, Type), uint> _offsets;

        internal BlobchegBuildLayout(BlobchegIdTable ids, Dictionary<(BlobchegNodeSo, Type), uint> offsets)
        {
            _ids = ids;
            _offsets = offsets;
        }

        /// <summary>Куда лечь файлу — та же папка, где лежат базы и роутеры.</summary>
        public string OutputDirectory => BlobchegBuild.OutputDirectory;

        /// <summary>Пишется ли отладочный контур. Гейт пре-билда снимает его для релизного плеера.</summary>
        public bool WithDebug => BlobchegBuild.WithDebug;

        /// <summary>Роутеры проекта в порядке имени.</summary>
        public IReadOnlyList<Type> Routers => BlobchegRouters.All;

        /// <summary>Базы роутера в порядке бит.</summary>
        public IReadOnlyList<Type> DomainsOf(Type router) => BlobchegRouters.DomainsOf(router);

        public string NameOf(Type router) => BlobchegRouters.NameOf(router);

        public ulong LayoutHashOf(Type router) => BlobchegRouters.LayoutHashOf(router);

        /// <summary>
        /// Строки роутера по номеру. <c>null</c> — дырка от удалённой ноды: строка в файле есть, но
        /// пустая, и её номер больше никому не выдаётся.
        /// </summary>
        public IReadOnlyList<BlobchegNodeSo> NodesOf(Type router) => _ids.NodesOf(router);

        /// <summary>Адрес записи ноды в базе. Записи нет — <c>false</c>, а не ноль.</summary>
        public bool TryOffset(BlobchegNodeSo node, Type domain, out uint offset)
            => _offsets.TryGetValue((node, domain), out offset);

        /// <summary>
        /// Манифест пишет ядро: правило «переписать, если хоть что-то разошлось с собранным» одно на
        /// все файлы пакета, и второй его копии в чужом проходе быть не должно.
        /// </summary>
        public void SyncManifest(string name, BlobchegFileKind kind, BlobchegNodeSo[] nodes,
            int recordCount, ulong contentHash, bool fileChanged, ref BlobchegBuildReport report)
            => BlobchegBuild.SyncManifest(name, kind, nodes, recordCount, contentHash, fileChanged, ref report);
    }
}
