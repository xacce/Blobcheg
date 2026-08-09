#if UNITY_EDITOR
using System.Collections.Generic;

namespace Blobcheg
{
    /// <summary>
    /// A counter of file rebuilds. A rebuild bumps the number of every file it rewrote, and whoever
    /// loaded that file into a world sees by the number that their base went stale and re-reads it.
    ///
    /// The key is the file name, not the domain name: a base and a router share no name, while both
    /// have a file, and it is exactly the file that gets rewritten.
    ///
    /// Editor only: in the player files are not rebuilt, there is nothing to watch. The numbers do not
    /// survive a domain reload — and must not: the worlds that remembered them die together with them.
    /// </summary>
    public static class BlobchegFileVersions
    {
        static readonly Dictionary<string, int> Versions = new Dictionary<string, int>();

        /// <summary>The file was rewritten. Called by a rebuild — once per changed file.</summary>
        public static void Bump(string fileName)
        {
            Versions.TryGetValue(fileName, out var version);
            Versions[fileName] = version + 1;
        }

        /// <summary>The current number of the file. Nobody rewrote the file — zero.</summary>
        public static int Of(string fileName)
        {
            Versions.TryGetValue(fileName, out var version);
            return version;
        }

        /// <summary>
        /// Whether the file was rewritten since the asker last read it. <paramref name="seen"/> is
        /// their own mark, updated right here, which is why the question is asked in one line and
        /// answers "no" the second time in a row.
        /// </summary>
        public static bool Changed(string fileName, ref int seen)
        {
            var version = Of(fileName);
            if (version == seen)
                return false;

            seen = version;
            return true;
        }
    }
}
#endif
