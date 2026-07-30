using System.Collections.Generic;

namespace Blobcheg
{
    /// <summary>
    /// Имена доменов по их ключам — только ради сообщений об ошибках. Отдельно от
    /// <see cref="BlobchegBases"/>, потому что тот класс читает Burst-код, а managed-словарь в нём
    /// утащил бы за собой статический конструктор и завалил компиляцию.
    ///
    /// Без этого «домен 22E12032EA346169 не поднят» — тупик: ключ FNV-64 не гуглится и в проекте
    /// нигде не написан.
    /// </summary>
    public static class BlobchegDomainNames
    {
        static readonly Dictionary<ulong, string> s_Names = new Dictionary<ulong, string>();

        public static void Remember(ulong domainKey, string name)
        {
            if (domainKey == 0 || string.IsNullOrEmpty(name))
                return;

            s_Names[domainKey] = name;
        }

        /// <summary>Имя домена, а если оно не встречалось — сам ключ, чтобы сообщение не осталось пустым.</summary>
        public static string Of(ulong domainKey)
            => s_Names.TryGetValue(domainKey, out var name) ? name : $"{domainKey:X16}";
    }
}
