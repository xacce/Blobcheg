using System;
using System.IO;
using System.Linq;
using Blobcheg.Authoring;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Blobcheg.AdvancedTests
{
    /// <summary>
    /// Общий стенд деструктивного набора. Держит две дороги в бинарник и обе — сквозные:
    ///
    /// 1. ЕДИТОРНЫЙ ЦИКЛ: ассеты нод → <see cref="BlobchegBuild.RebuildAll"/> → файлы в
    ///    StreamingAssets → ref/id-ассеты → чтение реинтерпретацией. Так живёт потребитель.
    /// 2. ФАЙЛОВЫЙ ЦИКЛ: <see cref="BlobchegWriter"/>/<see cref="BlobchegRouterWriter"/> → байты на
    ///    диске → <see cref="BlobchegBlob"/>/<see cref="BlobchegRouterBlob"/>. Тем же входом и
    ///    выходом, но без ассетов — иначе объёмные и граничные случаи (64 базы, 100k строк) стоили бы
    ///    десятков тысяч ассетов.
    ///
    /// Внутрь записей и внутрь раскладки набор не лезет: он смотрит только на то, что видно снаружи.
    /// </summary>
    public abstract class AdvancedFixture
    {
        /// <summary>Всё, что пересборка кладёт из-за доменов и роутеров этой сборки.</summary>
        static readonly string[] Artifacts =
        {
            "IAdvCombat", "IAdvCold", "IAdvLoose", "IAdvOther", "AdvRouter", "AdvAlienRouter",
        };

        protected string Folder;
        protected string Scratch;

        [SetUp]
        public void AdvancedSetUp()
        {
            // Папка своя на каждый тест: удаление ассетов отложенное, и переиспользованное имя
            // съедает ассет, созданный в ещё не удалённой папке.
            var name = "BlobchegAdvanced_" + Guid.NewGuid().ToString("N");
            Folder = "Assets/" + name;
            AssetDatabase.CreateFolder("Assets", name);

            Scratch = Path.Combine(Path.GetTempPath(), name);
            Directory.CreateDirectory(Scratch);

            AdvReentrantNodeSo.Forget();

            // Набор нарочно ломает пересборку, а хук импорта зовёт её сам, отложенным вызовом — то
            // есть между тестами. Прилетевшая оттуда ошибка не должна валить СОСЕДНИЙ тест: что
            // именно упало, каждый тест проверяет сам, своим Assert.Throws.
            LogAssert.ignoreFailingMessages = true;
        }

        [TearDown]
        public void AdvancedTearDown()
        {
            AssetDatabase.DeleteAsset(Folder);

            foreach (var name in Artifacts)
            {
                AssetDatabase.DeleteAsset(BlobchegBuild.ManifestFolder + "/" + name + ".asset");

                var file = FileOf(name);
                if (File.Exists(file))
                    File.Delete(file);
            }

            AssetDatabase.Refresh();

            try
            {
                if (Directory.Exists(Scratch))
                    Directory.Delete(Scratch, true);
            }
            catch (IOException)
            {
                // Мусор во временной папке ОС тест не роняет.
            }

            LogAssert.ignoreFailingMessages = false;
        }

        // ------------------------------------------------------------- ассеты

        protected T Node<T>(string name) where T : BlobchegNodeSo
        {
            var path = Folder + "/" + name + ".asset";
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<T>(), path);

            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            Assert.That(asset, Is.Not.Null, $"ассет '{path}' не создался — дальше проверять нечего");
            return asset;
        }

        /// <summary>Нода в подпапке — чтобы можно было завести две ноды с ОДИНАКОВЫМ именем.</summary>
        protected T NodeIn<T>(string subFolder, string name) where T : BlobchegNodeSo
        {
            if (!AssetDatabase.IsValidFolder(Folder + "/" + subFolder))
                AssetDatabase.CreateFolder(Folder, subFolder);

            var path = Folder + "/" + subFolder + "/" + name + ".asset";
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<T>(), path);

            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            Assert.That(asset, Is.Not.Null, $"ассет '{path}' не создался — дальше проверять нечего");
            return asset;
        }

        protected static void Dirty(UnityEngine.Object asset)
        {
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
        }

        protected static void Kill(UnityEngine.Object asset)
        {
            AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(asset));
            AssetDatabase.Refresh();
        }

        protected static BlobchegBuildReport Rebuild() => BlobchegBuild.RebuildAll();

        /// <summary>
        /// Снимает копию с <c>ref readonly</c>-возврата. Нужен там, где у записи нет ни одного поля,
        /// которое можно было бы тронуть выражением, — иначе вызов <c>Read</c> нечем «использовать».
        /// </summary>
        protected static T Copy<T>(in T value) where T : unmanaged => value;

        // ------------------------------------------------------------- адреса

        /// <summary>Оффсет записи ноды в домене — тем же путём, каким его берёт бейкер потребителя.</summary>
        protected static uint OffsetOf(BlobchegNodeSo node, string domainName)
        {
            var reference = BlobchegBuild.RefsOf(node).SingleOrDefault(r => r.DomainName == domainName);
            Assert.That(reference, Is.Not.Null, $"у ноды '{node.name}' нет ref-ассета домена '{domainName}'");
            return reference.offset;
        }

        protected static BlobchegRefSo RefOf(BlobchegNodeSo node, string domainName)
            => BlobchegBuild.RefsOf(node).Single(r => r.DomainName == domainName);

        protected static BlobchegId IdOf(BlobchegNodeSo node, string routerName)
        {
            var carrier = BlobchegBuild.IdsOf(node).SingleOrDefault(c => c.RouterName == routerName);
            Assert.That(carrier, Is.Not.Null, $"у ноды '{node.name}' нет носителя id роутера '{routerName}'");
            return new BlobchegId(carrier.id);
        }

        // ------------------------------------------------------------- файлы

        protected static string FileOf(string domainOrRouter)
            => Path.Combine(BlobchegBuild.OutputDirectory, BlobchegNaming.FileName(domainOrRouter));

        protected static byte[] Bytes(string domainOrRouter) => File.ReadAllBytes(FileOf(domainOrRouter));

        protected static void Overwrite(string domainOrRouter, byte[] file)
            => File.WriteAllBytes(FileOf(domainOrRouter), file);

        /// <summary>
        /// Перепечатывает header поверх изменённого тела. Нужен там, где ломают СМЫСЛ файла
        /// (пролог, флаги, длину), а не его целостность: без перепечатки первым сработал бы хеш, и
        /// тест проверил бы не то, что задумал.
        /// </summary>
        protected static void Reseal(byte[] file)
        {
            var flags = BlobchegBytes.ReadU16(file, 6);
            var debugOffset = BlobchegBytes.ReadU32(file, 12);
            var nameHash = BlobchegBytes.ReadU64(file, 24);
            BlobchegBytes.Seal(file, flags, debugOffset, nameHash);
        }

        protected static BlobchegBuffer BufferOf(string fileName)
            => BlobchegBuffer.From(
                File.ReadAllBytes(Path.Combine(BlobchegBuild.OutputDirectory, fileName)), Allocator.Persistent);

        protected static AdvCombatDb Combat() => new AdvCombatDb(BufferOf(AdvCombatDb.FileName));

        protected static AdvColdDb Cold() => new AdvColdDb(BufferOf(AdvColdDb.FileName));

        protected static AdvLooseDb Loose() => new AdvLooseDb(BufferOf(AdvLooseDb.FileName));

        protected static AdvOtherDb Other() => new AdvOtherDb(BufferOf(AdvOtherDb.FileName));

        protected static AdvRouter Router() => new AdvRouter(BufferOf(AdvRouter.FileName));

        protected static AdvAlienRouter OtherRouter() => new AdvAlienRouter(BufferOf(AdvAlienRouter.FileName));
    }
}
