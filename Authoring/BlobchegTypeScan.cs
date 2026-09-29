using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Blobcheg.Authoring
{
    /// <summary>
    /// The one place where the registries ask the project for its types. In the editor that is
    /// <c>TypeCache</c>: the registries are gathered anew after every reload, and a reflective walk over
    /// the whole domain would be paid for on every rebuild.
    ///
    /// Outside the editor there is no <c>TypeCache</c> — and no rebuild either. The walk stays for the
    /// sake of the contract alone: a node compiled into a player is an ordinary class, and the assembly
    /// it lives in must hold together without the editor.
    /// </summary>
    static class BlobchegTypeScan
    {
        public static IEnumerable<Type> WithAttribute<TAttribute>() where TAttribute : Attribute
        {
#if UNITY_EDITOR
            return TypeCache.GetTypesWithAttribute<TAttribute>();
#else
            return AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(Types)
                .Where(type => type.IsDefined(typeof(TAttribute), false));
#endif
        }

#if !UNITY_EDITOR
        static IEnumerable<Type> Types(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException loaded)
            {
                // An assembly half of which did not load still holds the half that did: the types that
                // arrived are exactly what the registry is after.
                return loaded.Types.Where(type => type != null);
            }
        }
#endif
    }
}
