using System;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace Blobcheg.Authoring
{
    /// <summary>
    /// Пересборка происходит сама. Кнопки нет: она даёт только возможность про себя забыть.
    /// Ловим импорт нод, вход в PlayMode и пре-билд.
    /// </summary>
    public sealed class BlobchegHooks : AssetPostprocessor
    {
        static bool _dirty;
        static bool _running;
        static bool _scheduled;

        [InitializeOnLoadMethod]
        static void Install()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            // Пересборка пишет носители сама и сама знает, что записала: считать эти импорты
            // чужой правкой — значит объявлять грязными все ноды, которые только что собрали.
            if (BlobchegBuild.Building)
                return;

            BlobchegCache.Touch(imported, deleted, moved, movedFrom);

            if (_running)
                return;

            if (TouchesNodes(imported) || TouchesDeleted(deleted) || TouchesNodes(moved) || TouchesDeleted(movedFrom))
                MarkDirty();
        }

        /// <summary>Пометить домены грязными вручную — из тестов и инструментов.</summary>
        public static void MarkDirty()
        {
            _dirty = true;
            if (_scheduled)
                return;

            _scheduled = true;
            EditorApplication.delayCall += () =>
            {
                _scheduled = false;
                RebuildIfDirty();
            };
        }

        public static void RebuildIfDirty()
        {
            if (!_dirty || _running || EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;

            _running = true;
            try
            {
                var report = BlobchegBuild.RebuildAll();
                _dirty = false;
                if (report.Changed)
                    Debug.Log($"Blobcheg: пересобрано — {report}");
            }
            finally
            {
                _running = false;
            }
        }

        static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.ExitingEditMode || !_dirty)
                return;

            try
            {
                RebuildIfDirty();
            }
            catch (Exception e)
            {
                // Ехать в PlayMode с несобранной базой нельзя: она либо поднимется целиком, либо нет.
                EditorApplication.isPlaying = false;
                Debug.LogException(e);
                throw;
            }
        }

        static bool TouchesNodes(string[] paths)
        {
            foreach (var path in paths)
            {
                if (IsOutput(path))
                    continue;

                var type = AssetDatabase.GetMainAssetTypeAtPath(path);
                if (type != null && typeof(BlobchegNodeSo).IsAssignableFrom(type))
                    return true;
            }

            return false;
        }

        static bool TouchesDeleted(string[] paths)
        {
            // Удалённый ассет уже не загрузить, тип не спросить. Пересборка идемпотентна, поэтому
            // лишний заход по любому удалённому .asset дешевле пропущенной ноды.
            foreach (var path in paths)
            {
                if (!IsOutput(path) && path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        static bool IsOutput(string path)
            => path.Replace('\\', '/').Contains("/StreamingAssets/" + BlobchegNaming.DefaultFolder + "/");
    }

    /// <summary>
    /// Пре-билд: блоб обязан быть собран, сжат и детерминирован до того, как поедет в плеер.
    ///
    /// Компакт именно здесь: дырки от удалённых нод — это байты, которые едут в билд ни за чем, а
    /// пересортировка двигает все адреса, и позволить её можно только там, где следом всё равно
    /// перепекается всё. В редакторе на неё есть отдельная команда, сама она не случается.
    /// </summary>
    public sealed class BlobchegBuildGate : BuildPlayerProcessor
    {
        // Не IPreprocessBuildWithReport: субсцены Entities печёт в PrepareForBuild
        // (EntitySceneBuildPlayerProcessor), а эта фаза идёт раньше пре-билд-колбэков. Сжать базу
        // после бейка — значит увезти в билд субсцены со старыми адресами и не заметить этого.
        // Отсюда и фаза, и порядок: раньше всех, кто печёт.
        public override int callbackOrder => -10000;

        public override void PrepareForBuild(BuildPlayerContext context)
        {
            Debug.Log("Blobcheg: пре-билд — компакт до бейка субсцен");

            BlobchegBuild.Compact();
            BlobchegBuild.RequireUpToDate("пре-билд");
        }
    }
}
