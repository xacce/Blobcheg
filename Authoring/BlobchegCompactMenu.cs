using UnityEditor;
using UnityEngine;

namespace Blobcheg.Authoring
{
    /// <summary>
    /// Команда компакта. Единственная кнопка во всём пакете, и она есть ровно потому, что компакт —
    /// это то, чего пересборка не имеет права сделать сама: он двигает все адреса и все id, а их
    /// уже запомнили запечённые субсцены и чужие сейвы. Человек зовёт её, когда готов перепечь.
    /// </summary>
    static class BlobchegCompactMenu
    {
        [MenuItem("Tools/Blobcheg/Сжать базы (компакт)")]
        static void Compact()
        {
            var ok = EditorUtility.DisplayDialog(
                "Blobcheg: компакт",
                "Дырки от удалённых нод исчезнут, но адреса и id будут выданы заново — все. " +
                "Всё, что их запомнило (запечённые субсцены, сохранения), после этого указывает не туда.\n\n" +
                "Перепечь субсцены придётся вручную.",
                "Сжать", "Отмена");

            if (!ok)
                return;

            var report = BlobchegBuild.Compact();
            Debug.Log($"Blobcheg: компакт — {report}");
        }
    }
}
