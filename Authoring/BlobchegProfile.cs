using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace Blobcheg.Authoring
{
    /// <summary>
    /// ВРЕМЕННАЯ разбивка времени пересборки по участкам: нужна, чтобы увидеть, где именно
    /// уходят секунды, а не гадать. Выключена по умолчанию, включается замером.
    /// </summary>
    public static class BlobchegProfile
    {
        public static bool Enabled;

        static readonly Dictionary<string, double> Totals = new Dictionary<string, double>();
        static readonly Dictionary<string, int> Counts = new Dictionary<string, int>();

        public struct Scope : IDisposable
        {
            public string Name;
            public Stopwatch Clock;

            public void Dispose()
            {
                if (Clock == null)
                    return;

                Clock.Stop();
                Add(Name, Clock.Elapsed.TotalMilliseconds);
            }
        }

        public static Scope Section(string name)
            => Enabled ? new Scope { Name = name, Clock = Stopwatch.StartNew() } : default;

        static void Add(string name, double ms)
        {
            if (!Totals.ContainsKey(name))
            {
                Totals[name] = 0;
                Counts[name] = 0;
            }

            Totals[name] += ms;
            Counts[name]++;
        }

        public static void Reset()
        {
            Totals.Clear();
            Counts.Clear();
        }

        /// <summary>Строки «мс ×вызовов участок», от дорогого к дешёвому.</summary>
        public static string Dump()
        {
            var text = new StringBuilder();
            foreach (var name in Totals.Keys.OrderByDescending(n => Totals[n]))
                text.AppendLine($"{Totals[name],9:F0} мс  ×{Counts[name],-6} {name}");

            return text.ToString();
        }
    }
}
