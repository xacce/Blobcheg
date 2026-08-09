using System;
using Blobcheg.Authoring;
using UnityEngine;

namespace Blobcheg.AdvancedTests
{
    // ------------------------------------------------------------------ domains
    //
    // Four domains and two routers. Two routers are needed because one of the main questions of the set
    // is "and what happens if an id handed out by ANOTHER router is slipped in"; with one router that
    // question cannot be asked. The router of every base is named explicitly: the assembly holds two of
    // them, and the rule "the single router of the assembly" does not work here.

    /// <summary>The combat base: in the AdvRouter router, member "combat".</summary>
    public interface IAdvCombat
    {
    }

    /// <summary>The cold base: in the AdvRouter router, member "cold".</summary>
    public interface IAdvCold
    {
    }

    /// <summary>A base outside any router — its nodes never have a BlobchegId at all.</summary>
    public interface IAdvLoose
    {
    }

    /// <summary>A base of the FOREIGN router AdvAlienRouter.</summary>
    public interface IAdvOther
    {
    }

    /// <summary>A domain declared by no base. A node that named it is obliged to fail.</summary>
    public interface IAdvUndeclared
    {
    }

    // ------------------------------------------------------------------ records

    public struct AdvGun : IAdvCombat
    {
        public float Ammo;
        public int Rpm;
    }

    /// <summary>A twin of <see cref="AdvGun"/>: the same size, a different type. The reinterpretation trap.</summary>
    public struct AdvGunTwin : IAdvCombat
    {
        public float Ammo;
        public int Rpm;
    }

    /// <summary>The type name sorts BEFORE AdvGun — adding such a node moves the offsets of the guns.</summary>
    public struct AdvArmor : IAdvCombat
    {
        public float Hp;
    }

    public enum AdvTier : byte
    {
        None = 0,
        Low = 1,
        High = 200,
    }

    /// <summary>A mix of bool, enum and alignment — the round trip is obliged to be byte for byte.</summary>
    public struct AdvMixed : IAdvCombat
    {
        public bool Flag;
        public AdvTier Tier;
        public double Weight;
        public short Small;
    }

    public struct AdvColdInfo : IAdvCold
    {
        /// <summary>Its own id, put by the node into the record itself: it is known BEFORE the write.</summary>
        public uint SelfId;

        public int Tier;

        /// <summary>The id of a neighbouring node — that is how one record references another.</summary>
        public uint LinkId;
    }

    public struct AdvLooseBlock : IAdvLoose
    {
        public long A;
        public long B;
    }

    public struct AdvOtherInfo : IAdvOther
    {
        public int V;
    }

    /// <summary>A record without a single field. In C# such a thing still weighs a byte — the question is what the layout does with it.</summary>
    public struct AdvEmptyRecord : IAdvLoose
    {
    }

    /// <summary>A record with a raw pointer inside. Formally unmanaged, in meaning garbage in the file.</summary>
    public unsafe struct AdvPointerRecord : IAdvLoose
    {
        public byte* Ptr;
        public long Tag;
    }

    /// <summary>A pointer not in plain sight but inside a struct field: an IntPtr is no better than a <c>byte*</c>.</summary>
    public struct AdvPointerHolder
    {
        public IntPtr Handle;
    }

    /// <summary>A record whose pointer lies at the second level of nesting.</summary>
    public struct AdvNestedPointerRecord : IAdvLoose
    {
        public long Head;
        public AdvPointerHolder Inner;
    }

    /// <summary>A quarter of the fat record — 64 B.</summary>
    public struct AdvChunk
    {
        public double A, B, C, D, E, F, G, H;
    }

    /// <summary>A fat record: 512 B in one type. Both its first byte and its last one are checked.</summary>
    public struct AdvFat : IAdvCombat
    {
        public AdvChunk C0, C1, C2, C3, C4, C5, C6, C7;
    }

    // ------------------------------------------------------------------ bases and routers

    [Blobcheg(typeof(IAdvCombat), "combat", Router = typeof(AdvRouter))]
    public partial struct AdvCombatDb
    {
    }

    [Blobcheg(typeof(IAdvCold), "cold", Router = typeof(AdvRouter))]
    public partial struct AdvColdDb
    {
    }

    [Blobcheg(typeof(IAdvOther), "other", Router = typeof(AdvAlienRouter))]
    public partial struct AdvOtherDb
    {
    }

    [Blobcheg(typeof(IAdvLoose))]
    public partial struct AdvLooseDb
    {
    }

    /// <summary>
    /// A second base OVER THE SAME domain. Absurd by construction: two facades, one file. It exists to
    /// check that the package either forbids that or both facades read one and the same thing.
    /// </summary>
    [Blobcheg(typeof(IAdvLoose))]
    public partial struct AdvLooseTwinDb
    {
    }

    [BlobchegRouter]
    public partial struct AdvRouter
    {
    }

    [BlobchegRouter]
    public partial struct AdvAlienRouter
    {
    }

    // ------------------------------------------------------------------ nodes

    /// <summary>A node in both bases of the router: a row with two bits.</summary>
    public sealed class AdvComboNodeSo : BlobchegNodeSo
    {
        public float ammo = 30f;
        public int rpm = 600;
        public int tier = 3;

        /// <summary>The neighbour whose id travels into the record. It may point at this same node.</summary>
        public BlobchegNodeSo link;

        public override Type[] OutTypes => new[] { typeof(IAdvCombat), typeof(IAdvCold) };

        public override void Write(ref BlobchegNodeWriter w)
        {
            w.Add(new AdvGun { Ammo = ammo, Rpm = rpm });
            w.Add(new AdvColdInfo
            {
                SelfId = w.IdIn<AdvRouter>().Value,
                Tier = tier,
                LinkId = link != null ? w.IdOf<AdvRouter>(link).Value : BlobchegId.NoneValue,
            });
        }
    }

    /// <summary>A node only in the cold base: a row with a hole where the combat bit would be.</summary>
    public sealed class AdvColdOnlyNodeSo : BlobchegNodeSo
    {
        public int tier = 9;

        public override Type[] OutTypes => new[] { typeof(IAdvCold) };

        public override void Write(ref BlobchegNodeWriter w)
            => w.Add(new AdvColdInfo { SelfId = w.IdIn<AdvRouter>().Value, Tier = tier, LinkId = BlobchegId.NoneValue });
    }

    public sealed class AdvArmorNodeSo : BlobchegNodeSo
    {
        public float hp = 100f;

        public override Type[] OutTypes => new[] { typeof(IAdvCombat) };

        public override void Write(ref BlobchegNodeWriter w) => w.Add(new AdvArmor { Hp = hp });
    }

    /// <summary>Writes the twin of the gun: the same bytes, a different type.</summary>
    public sealed class AdvTwinNodeSo : BlobchegNodeSo
    {
        public float ammo = 7f;
        public int rpm = 77;

        public override Type[] OutTypes => new[] { typeof(IAdvCombat) };

        public override void Write(ref BlobchegNodeWriter w) => w.Add(new AdvGunTwin { Ammo = ammo, Rpm = rpm });
    }

    public sealed class AdvMixedNodeSo : BlobchegNodeSo
    {
        public bool flag = true;
        public AdvTier tier = AdvTier.High;
        public double weight = -1234.5678;
        public short small = -31000;

        public override Type[] OutTypes => new[] { typeof(IAdvCombat) };

        public override void Write(ref BlobchegNodeWriter w)
            => w.Add(new AdvMixed { Flag = flag, Tier = tier, Weight = weight, Small = small });
    }

    public sealed class AdvFatNodeSo : BlobchegNodeSo
    {
        public double first = 1.5;
        public double last = -2.5;

        public override Type[] OutTypes => new[] { typeof(IAdvCombat) };

        public override void Write(ref BlobchegNodeWriter w)
        {
            var record = new AdvFat();
            record.C0.A = first;
            record.C7.H = last;
            w.Add(record);
        }
    }

    public sealed class AdvLooseNodeSo : BlobchegNodeSo
    {
        public long a = 1;
        public long b = 2;

        public override Type[] OutTypes => new[] { typeof(IAdvLoose) };

        public override void Write(ref BlobchegNodeWriter w) => w.Add(new AdvLooseBlock { A = a, B = b });
    }

    /// <summary>A raw record of arbitrary length, zero included.</summary>
    public sealed class AdvRawNodeSo : BlobchegNodeSo
    {
        public int size;
        public byte seed = 1;

        public override Type[] OutTypes => new[] { typeof(IAdvLoose) };

        public override void Write(ref BlobchegNodeWriter w)
        {
            var bytes = new byte[size];
            for (var i = 0; i < size; i++)
                bytes[i] = (byte)(seed + i);

            w.AddBytes<IAdvLoose>(bytes);
        }
    }

    public sealed class AdvOtherNodeSo : BlobchegNodeSo
    {
        public int v = 5;

        public override Type[] OutTypes => new[] { typeof(IAdvOther) };

        public override void Write(ref BlobchegNodeWriter w) => w.Add(new AdvOtherInfo { V = v });
    }

    /// <summary>A node in two routers at once: it never has a <c>w.Id</c>, only an <c>IdIn</c>.</summary>
    public sealed class AdvBothRoutersNodeSo : BlobchegNodeSo
    {
        public bool askSingleId;

        public uint LastMain;
        public uint LastOther;

        public override Type[] OutTypes => new[] { typeof(IAdvCombat), typeof(IAdvOther) };

        public override void Write(ref BlobchegNodeWriter w)
        {
            if (askSingleId)
            {
                // We ask for "the one and only id" of a node that has two. It is obliged to fail.
                LastMain = w.Id.Value;
            }

            LastMain = w.IdIn<AdvRouter>().Value;
            LastOther = w.IdIn<AdvAlienRouter>().Value;

            w.Add(new AdvGun { Ammo = 1f, Rpm = 1 });
            w.Add(new AdvOtherInfo { V = 2 });
        }
    }

    /// <summary>It declared a domain and wrote nothing into it.</summary>
    public sealed class AdvSilentNodeSo : BlobchegNodeSo
    {
        public override Type[] OutTypes => new[] { typeof(IAdvCombat) };

        public override void Write(ref BlobchegNodeWriter w)
        {
        }
    }

    /// <summary>It writes into a domain that is not in its OutTypes.</summary>
    public sealed class AdvStrayNodeSo : BlobchegNodeSo
    {
        public override Type[] OutTypes => new[] { typeof(IAdvCold) };

        public override void Write(ref BlobchegNodeWriter w)
        {
            w.Add(new AdvColdInfo { SelfId = w.IdIn<AdvRouter>().Value });
            w.Add(new AdvGun { Ammo = 1f, Rpm = 1 });
        }
    }

    /// <summary>It writes into one domain twice.</summary>
    public sealed class AdvDoubleNodeSo : BlobchegNodeSo
    {
        public override Type[] OutTypes => new[] { typeof(IAdvCombat) };

        public override void Write(ref BlobchegNodeWriter w)
        {
            w.Add(new AdvGun { Ammo = 1f, Rpm = 1 });
            w.Add(new AdvGun { Ammo = 2f, Rpm = 2 });
        }
    }

    /// <summary>It declared no domain at all.</summary>
    public sealed class AdvNoOutTypesNodeSo : BlobchegNodeSo
    {
        public override Type[] OutTypes => Array.Empty<Type>();

        public override void Write(ref BlobchegNodeWriter w)
        {
        }
    }

    /// <summary>It named a domain that is declared by no base.</summary>
    public sealed class AdvUndeclaredNodeSo : BlobchegNodeSo
    {
        public override Type[] OutTypes => new[] { typeof(IAdvUndeclared) };

        public override void Write(ref BlobchegNodeWriter w)
        {
        }
    }

    /// <summary>It fails the rebuild from the middle of <c>Write</c>.</summary>
    public sealed class AdvThrowNodeSo : BlobchegNodeSo
    {
        public bool armed = true;

        public override Type[] OutTypes => new[] { typeof(IAdvCombat) };

        public override void Write(ref BlobchegNodeWriter w)
        {
            w.Add(new AdvGun { Ammo = 3f, Rpm = 3 });

            if (armed)
                throw new InvalidOperationException("AdvThrowNodeSo: failing the rebuild on purpose");
        }
    }

    /// <summary>
    /// It calls the rebuild from <c>Write</c> — that is, from the middle of a rebuild. The depth is
    /// limited by the NODE itself and not by the package: without that limiter the test would crash the
    /// editor with a stack overflow and no report would be left at all.
    /// </summary>
    public sealed class AdvReentrantNodeSo : BlobchegNodeSo
    {
        /// <summary>How many times the rebuild entered itself. Zero means the package rejected the reentrancy.</summary>
        public static int Reentered;

        static int _depth;

        /// <summary>
        /// It must not be called <c>Reset</c>: on a ScriptableObject that is a magic name, and Unity calls
        /// it itself when an instance is created — on a static method that is a console error on every
        /// CreateInstance.
        /// </summary>
        public static void Forget()
        {
            Reentered = 0;
            _depth = 0;
        }

        public override Type[] OutTypes => new[] { typeof(IAdvCombat) };

        public override void Write(ref BlobchegNodeWriter w)
        {
            if (_depth == 0)
            {
                _depth++;
                try
                {
                    BlobchegBuild.RebuildAll();
                    Reentered++;
                }
                catch (Exception)
                {
                    // The package rejected the nested rebuild — that is the expected behaviour.
                }
                finally
                {
                    _depth--;
                }
            }

            w.Add(new AdvGun { Ammo = 4f, Rpm = 4 });
        }
    }

    /// <summary>It puts a raw pointer into the file.</summary>
    public sealed class AdvPointerNodeSo : BlobchegNodeSo
    {
        public override Type[] OutTypes => new[] { typeof(IAdvLoose) };

        public override unsafe void Write(ref BlobchegNodeWriter w)
            => w.Add(new AdvPointerRecord { Ptr = (byte*)0xDEADBEEF, Tag = 42 });
    }

    /// <summary>It puts into the file a pointer hidden at the second level of nesting.</summary>
    public sealed class AdvNestedPointerNodeSo : BlobchegNodeSo
    {
        public override Type[] OutTypes => new[] { typeof(IAdvLoose) };

        public override void Write(ref BlobchegNodeWriter w)
            => w.Add(new AdvNestedPointerRecord { Head = 1, Inner = new AdvPointerHolder { Handle = new IntPtr(0x1234) } });
    }

    /// <summary>A record without fields.</summary>
    public sealed class AdvEmptyRecordNodeSo : BlobchegNodeSo
    {
        public override Type[] OutTypes => new[] { typeof(IAdvLoose) };

        public override void Write(ref BlobchegNodeWriter w) => w.Add(new AdvEmptyRecord());
    }

    /// <summary>One record of megabytes.</summary>
    public sealed class AdvHugeNodeSo : BlobchegNodeSo
    {
        public int megabytes = 2;

        public override Type[] OutTypes => new[] { typeof(IAdvLoose) };

        public override void Write(ref BlobchegNodeWriter w)
        {
            var bytes = new byte[megabytes * 1024 * 1024];
            for (var i = 0; i < bytes.Length; i += 4096)
                bytes[i] = (byte)(i / 4096);

            bytes[bytes.Length - 1] = 0xFE;
            w.AddBytes<IAdvLoose>(bytes);
        }
    }

    // ------------------------------------------------------------------ arrays inside a record

    /// <summary>A record with a variable-length array.</summary>
    public struct AdvWeights : IAdvLoose
    {
        public int Rolls;
        public BlobchegArray<float> Weights;
    }

    /// <summary>The array is hidden at the second level of nesting — a literal is obliged to be rejected that way too.</summary>
    public struct AdvDeepArrayHolder
    {
        public BlobchegArray<int> Cells;
    }

    public struct AdvDeepArrayRecord : IAdvLoose
    {
        public long Head;
        public AdvDeepArrayHolder Inner;
    }

    /// <summary>A tree element: it carries an array of elements of ITS OWN type. Recursion by construction.</summary>
    public struct AdvTreeNode
    {
        public int Value;
        public BlobchegArray<AdvTreeNode> Children;
    }

    public struct AdvTree : IAdvLoose
    {
        public BlobchegArray<AdvTreeNode> Roots;
    }

    /// <summary>A record that has nothing in the cold domain except the array.</summary>
    public struct AdvColdCells : IAdvCold
    {
        public BlobchegArray<int> Cells;
    }

    /// <summary>An array of settable length — the knob for edits of length and volume.</summary>
    public sealed class AdvWeightsNodeSo : BlobchegNodeSo
    {
        public int count = 3;

        public override Type[] OutTypes => new[] { typeof(IAdvLoose) };

        public override void Write(ref BlobchegNodeWriter w)
        {
            var b = w.Begin<AdvWeights>();
            b.Root.Rolls = count;

            var weights = b.Allocate(ref b.Root.Weights, count);
            for (var i = 0; i < weights.Length; i++)
                weights[i] = i * 0.5f;

            b.End();
        }
    }

    /// <summary>A record with an array written as a struct literal. It is obliged to be rejected, even with an empty array.</summary>
    public sealed class AdvArrayLiteralNodeSo : BlobchegNodeSo
    {
        public override Type[] OutTypes => new[] { typeof(IAdvLoose) };

        public override void Write(ref BlobchegNodeWriter w)
            => w.Add(new AdvDeepArrayRecord { Head = 1 });
    }

    /// <summary>It writes into an array window AFTER End — by the experience of other builders that is "still allowed".</summary>
    public sealed class AdvLateWindowNodeSo : BlobchegNodeSo
    {
        public override Type[] OutTypes => new[] { typeof(IAdvLoose) };

        public override void Write(ref BlobchegNodeWriter w)
        {
            var b = w.Begin<AdvWeights>();
            var weights = b.Allocate(ref b.Root.Weights, 2);
            weights[0] = 1f;
            b.End();

            // The chunk memory is already freed — this line is obliged to throw rather than write into it.
            weights[1] = 2f;
        }
    }

    /// <summary>Write fails between Begin and End: ITS error is the one obliged to reach the base.</summary>
public sealed class AdvThrowingBuilderNodeSo : BlobchegNodeSo
    {
        public const string Cry = "failed in the middle of the array on purpose";

        public override Type[] OutTypes => new[] { typeof(IAdvLoose) };

        public override void Write(ref BlobchegNodeWriter w)
        {
            var b = w.Begin<AdvWeights>();
            b.Allocate(ref b.Root.Weights, 4);
            throw new InvalidOperationException(Cry);
        }
    }

    /// <summary>A field of one builder is fed into the Allocate of another. The records are different, after all.</summary>
    public sealed class AdvCrossBuilderNodeSo : BlobchegNodeSo
    {
        public override Type[] OutTypes => new[] { typeof(IAdvLoose), typeof(IAdvCold) };

        public override void Write(ref BlobchegNodeWriter w)
        {
            var loose = w.Begin<AdvWeights>();
            var cold = w.Begin<AdvColdCells>();

            // A field from a FOREIGN record: an offset between two different blocks means nothing.
            cold.Allocate(ref loose.Root.Weights, 1);
        }
    }

    /// <summary>A builder without a single Allocate: a forgotten array field is obliged to read as empty.</summary>
    public sealed class AdvForgottenAllocateNodeSo : BlobchegNodeSo
    {
        public override Type[] OutTypes => new[] { typeof(IAdvLoose) };

        public override void Write(ref BlobchegNodeWriter w)
        {
            var b = w.Begin<AdvWeights>();
            b.Root.Rolls = 9;
            b.End();
        }
    }

    /// <summary>A two-level tree over a recursive element type.</summary>
    public sealed class AdvTreeNodeSo : BlobchegNodeSo
    {
        public override Type[] OutTypes => new[] { typeof(IAdvLoose) };

        public override void Write(ref BlobchegNodeWriter w)
        {
            var b = w.Begin<AdvTree>();
            var roots = b.Allocate(ref b.Root.Roots, 2);
            roots[0].Value = 1;
            roots[1].Value = 2;

            var left = b.Allocate(ref roots[0].Children, 2);
            left[0].Value = 11;
            left[1].Value = 12;

            var deep = b.Allocate(ref left[1].Children, 1);
            deep[0].Value = 121;

            b.End();
        }
    }
}
