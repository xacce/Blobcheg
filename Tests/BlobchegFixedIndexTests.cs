using System;
using System.IO;
using System.Linq;
using Blobcheg.Authoring;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace Blobcheg.Tests
{
    /// <summary>Домен детерминированного роутера. Свой, чтобы не смешиваться с обычным.</summary>
    public interface ITestGridData
    {
    }

    public struct TestGridInfo : ITestGridData
    {
        /// <summary>Свой id, положенный нодой в запись: он известен до записи и здесь тоже.</summary>
        public uint SelfId;

        public int Tier;
    }

    /// <summary>Имя члена — <c>grid</c>, а не <c>fixed</c>: из имени члена кодоген делает поле строки.</summary>
    [Blobcheg(typeof(ITestGridData), "grid", Router = typeof(TestFixedRouter))]
    public partial struct TestGridDb
    {
    }

    /// <summary>Роутер, чьи номера строк объявляют ноды.</summary>
    [BlobchegRouter(FixedIndex = true)]
    public partial struct TestFixedRouter
    {
    }

    /// <summary>Нода, объявляющая свой номер полем.</summary>
    public sealed class TestFixedNodeSo : BlobchegNodeSo, IBlobchegIndexed
    {
        public uint index;
        public int tier = 1;

        public uint Index => index;

        public override Type[] OutTypes => new[] { typeof(ITestGridData) };

        public override void Write(ref BlobchegNodeWriter writer)
            => writer.Add(new TestGridInfo { SelfId = writer.Id.Value, Tier = tier });
    }

    /// <summary>Нода того же домена, но без интерфейса — на ней проверяется отказ.</summary>
    public sealed class TestBlindNodeSo : BlobchegNodeSo
    {
        public override Type[] OutTypes => new[] { typeof(ITestGridData) };

        public override void Write(ref BlobchegNodeWriter writer)
            => writer.Add(new TestGridInfo { SelfId = writer.Id.Value, Tier = 0 });
    }

    /// <summary>
    /// Детерминированный роутер: номер строки объявляет нода, пересборка его только собирает и
    /// проверяет. Носитель id при этом производный, а не источник правды.
    /// </summary>
    public sealed class BlobchegFixedIndexTests
    {
        string _folder;

        [SetUp]
        public void SetUp()
        {
            // Папка своя на каждый тест: удаление ассетов отложенное, и переиспользованное имя
            // съедает ассет, созданный в ещё не удалённой папке.
            var name = "BlobchegFixedTemp_" + Guid.NewGuid().ToString("N");
            _folder = "Assets/" + name;
            AssetDatabase.CreateFolder("Assets", name);
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(_folder);
            BlobchegTestArtifacts.Wipe();
        }

        TestFixedNodeSo Node(string name, uint index)
        {
            var path = _folder + "/" + name + ".asset";
            var created = ScriptableObject.CreateInstance<TestFixedNodeSo>();
            created.index = index;
            AssetDatabase.CreateAsset(created, path);

            var asset = AssetDatabase.LoadAssetAtPath<TestFixedNodeSo>(path);
            Assert.That(asset, Is.Not.Null, $"ассет '{path}' не создался — дальше проверять нечего");
            return asset;
        }

        static BlobchegId IdOf(BlobchegNodeSo node)
        {
            var carrier = BlobchegBuild.IdsOf(node)
                .Single(c => c.RouterName == TestFixedRouter.RouterName);

            return new BlobchegIdRef<TestFixedRouter>(carrier).Id;
        }

        static TestFixedRouter LoadRouter()
        {
            var path = Path.Combine(BlobchegBuild.OutputDirectory, TestFixedRouter.FileName);
            Assert.That(File.Exists(path), Is.True, "файл роутера должен лечь в StreamingAssets");
            return new TestFixedRouter(BlobchegBuffer.From(File.ReadAllBytes(path), Allocator.Persistent));
        }

        static TestGridDb LoadGrid()
            => new TestGridDb(BlobchegBuffer.From(
                File.ReadAllBytes(Path.Combine(BlobchegBuild.OutputDirectory, TestGridDb.FileName)),
                Allocator.Persistent));

        [Test]
        public void Реестр_знает_какой_роутер_детерминированный()
        {
            Assert.That(BlobchegRouters.IsFixed(typeof(TestFixedRouter)), Is.True);
            Assert.That(BlobchegRouters.IsFixed(typeof(TestGameRouter)), Is.False);
            Assert.DoesNotThrow(() => BlobchegRouters.RequireCodeGenAgrees(typeof(TestFixedRouter)),
                "кодоген аргументов [BlobchegRouter] не читает — LayoutHash от флага не зависит");
        }
    }
}
