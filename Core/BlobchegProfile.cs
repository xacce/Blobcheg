using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Debug = UnityEngine.Debug;

namespace Blobcheg
{
    public static class BlobchegProfile
    {
        public static bool Enabled = true;

        const double SilentBelowMs = 1;

        static readonly Dictionary<string, double> Totals = new Dictionary<string, double>();
        static readonly Dictionary<string, int> Counts = new Dictionary<string, int>();
        static readonly Work Silent = new Work(null);

        static int _depth;

        // A class and not a struct: `using var` is a read-only local, and a note set on a copy is lost.
        public sealed class Work : IDisposable
        {
            readonly string _what;
            readonly Stopwatch _clock;
            string _tail;

            internal Work(string what)
            {
                _what = what;
                if (what != null)
                    _clock = Stopwatch.StartNew();
            }

            public void Note(string tail)
            {
                if (_clock != null)
                    _tail = tail;
            }

            public void Dispose()
            {
                if (_clock == null)
                    return;

                _clock.Stop();
                End(_what, _clock.Elapsed.TotalMilliseconds, _tail);
            }
        }

        public static Work Begin(string what)
        {
            if (!Enabled)
                return Silent;

            _depth++;
            return new Work(what);
        }

        public struct Scope : IDisposable
        {
            internal string Name;
            internal Stopwatch Clock;

            public void Dispose()
            {
                if (Clock == null)
                    return;

                Clock.Stop();
                Add(Name, Clock.Elapsed.TotalMilliseconds);
            }
        }

        public static void Say(string what)
        {
            if (Enabled)
                Debug.Log("Blobcheg: " + what);
        }

        public static Scope Section(string name)
            => Enabled ? new Scope { Name = name, Clock = Stopwatch.StartNew() } : default;

        static void End(string what, double ms, string tail)
        {
            if (_depth > 0)
                _depth--;

            // The sections belong to the outermost unit: the pre-build runs a rebuild inside a rebuild.
            string breakdown = null;
            if (_depth == 0 && Totals.Count > 0)
            {
                breakdown = Dump();
                Reset();
            }

            if (breakdown == null && ms < SilentBelowMs)
                return;

            var text = new StringBuilder("Blobcheg: ").Append(what)
                .Append(" — ").Append(ms.ToString("F0")).Append(" ms");

            if (tail != null)
                text.Append(" — ").Append(tail);

            if (breakdown != null)
                text.AppendLine().Append(breakdown);

            Debug.Log(text.ToString());
        }

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

        public static string Dump()
        {
            var text = new StringBuilder();
            foreach (var name in Totals.Keys.OrderByDescending(n => Totals[n]))
                text.AppendLine($"{Totals[name],9:F0} ms  ×{Counts[name],-6} {name}");

            return text.ToString();
        }
    }
}
