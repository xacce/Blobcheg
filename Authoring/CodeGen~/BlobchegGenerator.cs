using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Blobcheg.CodeGen
{
    /// <summary>
    /// Adds to the partial of a base by <c>[Blobcheg(typeof(IDomain))]</c> and to the partial of a
    /// router by <c>[BlobchegRouter]</c>. The generator emits ONLY structs and systems: it cannot emit a
    /// ScriptableObject — a type out of the codegen has no MonoScript, and such a type will never become
    /// an asset.
    /// </summary>
    [Generator]
    public sealed class BlobchegGenerator : IIncrementalGenerator
    {
        const string DbAttributeName = "Blobcheg.BlobchegAttribute";
        const string RouterAttributeName = "Blobcheg.BlobchegRouterAttribute";
        const string HashesAttributeName = "Blobcheg.BlobchegHashesAttribute";
        const string EntitiesAssembly = "Blobcheg.Entities";
        const string ComponentData = "Unity.Entities.IComponentData";
        const string DisableAutoCreation = "Unity.Entities.DisableAutoCreationAttribute";

        static readonly DiagnosticDescriptor NotPartial = new DiagnosticDescriptor(
            "BCHG001", "A base is obliged to be partial",
            "Struct '{0}' is marked [Blobcheg] but is not declared partial — there is nothing to add to it",
            "Blobcheg", DiagnosticSeverity.Error, true);

        static readonly DiagnosticDescriptor Nested = new DiagnosticDescriptor(
            "BCHG002", "A base cannot be a nested type",
            "Struct '{0}' is marked [Blobcheg] but is nested in another type — move it outside",
            "Blobcheg", DiagnosticSeverity.Error, true);

        static readonly DiagnosticDescriptor BadDomain = new DiagnosticDescriptor(
            "BCHG003", "A domain is obliged to be a marker interface",
            "The [Blobcheg] on struct '{0}' was given '{1}' — a domain is declared by an interface",
            "Blobcheg", DiagnosticSeverity.Error, true);

        static readonly DiagnosticDescriptor NoRouter = new DiagnosticDescriptor(
            "BCHG004", "The router is undetermined",
            "Struct '{0}' has a router member name set, but no router is chosen: {1}. " +
            "Set Router = typeof(...) in [Blobcheg]",
            "Blobcheg", DiagnosticSeverity.Error, true);

        static readonly DiagnosticDescriptor RouterNotPartial = new DiagnosticDescriptor(
            "BCHG005", "A router is obliged to be partial and not nested",
            "Struct '{0}' is marked [BlobchegRouter] but is not declared partial or is nested in another type",
            "Blobcheg", DiagnosticSeverity.Error, true);

        static readonly DiagnosticDescriptor RouterClash = new DiagnosticDescriptor(
            "BCHG006", "The router is assembled from contradictory bases",
            "Router '{0}': {1}",
            "Blobcheg", DiagnosticSeverity.Error, true);

        static readonly DiagnosticDescriptor TooManyDomains = new DiagnosticDescriptor(
            "BCHG007", "More than 64 bases in a router",
            "Router '{0}' is assembled from {1} bases — the ceiling is 64. That is not \"too few bits\" but a badly sliced project",
            "Blobcheg", DiagnosticSeverity.Error, true);

        static readonly DiagnosticDescriptor NoEntitiesReference = new DiagnosticDescriptor(
            "BCHG008", "No reference to Blobcheg.Entities",
            "Struct '{0}' is declared IComponentData — a boot system is emitted for it, and the assembly does " +
            "not reference Blobcheg.Entities. Add the reference or drop IComponentData",
            "Blobcheg", DiagnosticSeverity.Error, true);

        static readonly DiagnosticDescriptor HashesNotPartial = new DiagnosticDescriptor(
            "BCHG009", "A hash table is obliged to be partial and not nested",
            "Struct '{0}' is marked [BlobchegHashes] but is not declared partial or is nested in another type",
            "Blobcheg", DiagnosticSeverity.Error, true);

        static readonly DiagnosticDescriptor HashesNoRouter = new DiagnosticDescriptor(
            "BCHG010", "The hash table references something that is not a router",
            "The [BlobchegHashes] on struct '{0}' was given '{1}': {2}",
            "Blobcheg", DiagnosticSeverity.Error, true);

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var model = context.CompilationProvider.Select(static (compilation, _) => Model.Build(compilation));

            var databases = context.SyntaxProvider.ForAttributeWithMetadataName(
                DbAttributeName,
                static (node, _) => node is StructDeclarationSyntax,
                static (ctx, _) => ctx);

            var routers = context.SyntaxProvider.ForAttributeWithMetadataName(
                RouterAttributeName,
                static (node, _) => node is StructDeclarationSyntax,
                static (ctx, _) => ctx);

            var hashes = context.SyntaxProvider.ForAttributeWithMetadataName(
                HashesAttributeName,
                static (node, _) => node is StructDeclarationSyntax,
                static (ctx, _) => ctx);

            context.RegisterSourceOutput(databases.Combine(model), static (source, pair) => EmitDatabase(source, pair.Left, pair.Right));
            context.RegisterSourceOutput(routers.Combine(model), static (source, pair) => EmitRouter(source, pair.Left, pair.Right));
            context.RegisterSourceOutput(hashes.Combine(model), static (source, pair) => EmitHashes(source, pair.Left, pair.Right));
        }

        // ---------------------------------------------------------------- model

        sealed class DbInfo
        {
            public string DbName;
            public string DomainMetadata;
            public string Member;
            public string RouterName;

            /// <summary>The router is named, but it is not in this assembly — so it is in a foreign one.</summary>
            public string RouterElsewhere;
        }

        sealed class RouterInfo
        {
            public string Name;
            public readonly List<DbInfo> Dbs = new List<DbInfo>();
        }

        /// <summary>
        /// The bases and routers of THIS assembly. Foreign assemblies are deliberately not looked at: a
        /// router's generator computes the bits from the list of bases, and it only sees its own
        /// compilation — a base in an assembly that references the router is invisible to it, and the
        /// bits would come out computed over less than all of them.
        ///
        /// Hence the rule: a router and its bases lie in one assembly. Reversing the reference order does
        /// not help — a base that sees the router is invisible to the router by construction.
        /// </summary>
        sealed class Model
        {
            public readonly List<DbInfo> Dbs = new List<DbInfo>();
            public readonly Dictionary<string, RouterInfo> Routers = new Dictionary<string, RouterInfo>();
            public bool HasEntities;

            public static Model Build(Compilation compilation)
            {
                var model = new Model
                {
                    HasEntities = compilation.ReferencedAssemblyNames.Any(a => a.Name == EntitiesAssembly),
                };

                var dbAttribute = compilation.GetTypeByMetadataName(DbAttributeName);
                var routerAttribute = compilation.GetTypeByMetadataName(RouterAttributeName);
                if (dbAttribute == null || routerAttribute == null)
                    return model;

                var raw = new List<(INamedTypeSymbol Type, AttributeData Attribute)>();

                foreach (var type in AllTypes(compilation))
                {
                    foreach (var attribute in type.GetAttributes())
                    {
                        if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, routerAttribute))
                        {
                            var name = type.Name;
                            if (!model.Routers.ContainsKey(name))
                                model.Routers.Add(name, new RouterInfo { Name = name });
                        }
                        else if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, dbAttribute))
                        {
                            raw.Add((type, attribute));
                        }
                    }
                }

                foreach (var pair in raw)
                {
                    var domain = pair.Attribute.ConstructorArguments.Length > 0
                        ? pair.Attribute.ConstructorArguments[0].Value as INamedTypeSymbol
                        : null;

                    if (domain == null)
                        continue;

                    var info = new DbInfo
                    {
                        DbName = pair.Type.Name,
                        DomainMetadata = MetadataName(domain),
                        Member = pair.Attribute.ConstructorArguments.Length > 1
                            ? pair.Attribute.ConstructorArguments[1].Value as string
                            : null,
                        RouterName = RouterNameOf(pair.Attribute),
                    };

                    if (string.IsNullOrEmpty(info.Member))
                    {
                        info.Member = null;
                        info.RouterName = null;
                    }
                    else if (info.RouterName == null && model.Routers.Count == 1)
                    {
                        info.RouterName = model.Routers.Keys.First();
                    }

                    if (info.RouterName != null && !model.Routers.ContainsKey(info.RouterName))
                    {
                        info.RouterElsewhere = info.RouterName;
                        info.RouterName = null;
                    }

                    model.Dbs.Add(info);

                    if (info.RouterName != null && model.Routers.TryGetValue(info.RouterName, out var router))
                        router.Dbs.Add(info);
                }

                foreach (var router in model.Routers.Values)
                    router.Dbs.Sort((a, b) => string.CompareOrdinal(a.DomainMetadata, b.DomainMetadata));

                return model;
            }

            public DbInfo Find(string dbName) => Dbs.FirstOrDefault(db => db.DbName == dbName);

            static string RouterNameOf(AttributeData attribute)
            {
                foreach (var named in attribute.NamedArguments)
                {
                    if (named.Key == "Router" && named.Value.Value is INamedTypeSymbol router)
                        return router.Name;
                }

                return null;
            }

            static IEnumerable<INamedTypeSymbol> AllTypes(Compilation compilation)
                => Walk(compilation.Assembly.GlobalNamespace);

            static IEnumerable<INamedTypeSymbol> Walk(INamespaceSymbol space)
            {
                foreach (var type in space.GetTypeMembers())
                    yield return type;

                foreach (var nested in space.GetNamespaceMembers())
                {
                    foreach (var type in Walk(nested))
                        yield return type;
                }
            }
        }

        // ---------------------------------------------------------------- bases

        static void EmitDatabase(SourceProductionContext source, GeneratorAttributeSyntaxContext ctx, Model model)
        {
            var symbol = (INamedTypeSymbol)ctx.TargetSymbol;
            var declaration = (StructDeclarationSyntax)ctx.TargetNode;

            if (!declaration.Modifiers.Any(m => m.ValueText == "partial"))
            {
                source.ReportDiagnostic(Diagnostic.Create(NotPartial, declaration.Identifier.GetLocation(), symbol.Name));
                return;
            }

            if (symbol.ContainingType != null)
            {
                source.ReportDiagnostic(Diagnostic.Create(Nested, declaration.Identifier.GetLocation(), symbol.Name));
                return;
            }

            var attribute = ctx.Attributes[0];
            if (attribute.ConstructorArguments.Length < 1
                || !(attribute.ConstructorArguments[0].Value is INamedTypeSymbol domain))
                return;

            if (domain.TypeKind != TypeKind.Interface)
            {
                source.ReportDiagnostic(Diagnostic.Create(
                    BadDomain, declaration.Identifier.GetLocation(), symbol.Name, domain.Name));
                return;
            }

            var info = model.Find(symbol.Name);
            if (info != null && info.Member != null && info.RouterName == null)
            {
                source.ReportDiagnostic(Diagnostic.Create(
                    NoRouter, declaration.Identifier.GetLocation(), symbol.Name,
                    info.RouterElsewhere != null
                        ? $"router '{info.RouterElsewhere}' lies in another assembly, and a router and its bases are obliged to be in one"
                        : model.Routers.Count == 0
                            ? "this assembly has no [BlobchegRouter] at all"
                            : $"the assembly holds {model.Routers.Count} routers at once"));
                return;
            }

            var boot = Boot(source, symbol, declaration, model, out var autoCreate);

            var text = new StringBuilder();
            var domainFull = domain.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            Open(text, symbol, out var space);

            // No unsafe: a type with a pointer inside can be held as a field in safe code too, and there
            // is no reason to demand allowUnsafeCode from the consumer just to declare a base.
            text.Append("    ").Append(Access(symbol)).Append(" partial struct ")
                .Append(symbol.Name).AppendLine(" : global::System.IDisposable");
            text.AppendLine("    {");
            text.Append("        public const string DomainName = \"").Append(domain.Name).AppendLine("\";");
            text.AppendLine();
            text.AppendLine("        global::Blobcheg.BlobchegBlob __blob;");
            text.AppendLine();
            text.Append("        public ").Append(symbol.Name).AppendLine("(global::Blobcheg.BlobchegBuffer buffer)");
            text.AppendLine("        {");
            text.AppendLine("            __blob = new global::Blobcheg.BlobchegBlob(buffer, DomainName);");
            text.AppendLine("        }");
            text.AppendLine();
            text.AppendLine("        /// <summary>The file name of the base — the transport asks for the same one.</summary>");
            text.AppendLine("        public static string FileName => global::Blobcheg.BlobchegNaming.FileName(DomainName);");
            text.AppendLine();
            text.AppendLine("        public bool IsCreated => __blob.IsCreated;");
            text.AppendLine();
            text.AppendLine("        public int Length => __blob.Length;");
            text.AppendLine();
            text.AppendLine("        /// <summary>Whether the file carries a debug contour. A release player never has one.</summary>");
            text.AppendLine("        public bool HasDebug => __blob.HasDebug;");
            text.AppendLine();
            text.AppendLine("        /// <summary>Type and node names by offset — for editor tools only.</summary>");
            text.AppendLine("        public void Describe(uint offset, out string typeName, out string nodeName)");
            text.AppendLine("            => __blob.Describe(offset, out typeName, out nodeName);");
            text.AppendLine();
            text.AppendLine("        /// <summary>A foreign domain does not compile here — this is the only check that always works.</summary>");
            text.AppendLine("        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
            text.Append("        public ref readonly T Read<T>(uint offset) where T : unmanaged, ")
                .Append(domainFull).AppendLine();
            text.AppendLine("            => ref __blob.Read<T>(offset);");
            text.AppendLine();
            text.AppendLine("        public void Dispose() => __blob.Dispose();");
            text.AppendLine("    }");

            if (boot)
                EmitBootSystem(text, symbol.Name, Access(symbol), autoCreate);

            Close(text, space);

            source.AddSource(symbol.Name + ".blobcheg.g.cs", SourceText.From(text.ToString(), Encoding.UTF8));
        }

        // ---------------------------------------------------------------- router

        static void EmitRouter(SourceProductionContext source, GeneratorAttributeSyntaxContext ctx, Model model)
        {
            var symbol = (INamedTypeSymbol)ctx.TargetSymbol;
            var declaration = (StructDeclarationSyntax)ctx.TargetNode;

            if (!declaration.Modifiers.Any(m => m.ValueText == "partial") || symbol.ContainingType != null)
            {
                source.ReportDiagnostic(Diagnostic.Create(RouterNotPartial, declaration.Identifier.GetLocation(), symbol.Name));
                return;
            }

            if (!model.Routers.TryGetValue(symbol.Name, out var router))
                return;

            if (router.Dbs.Count == 0)
            {
                source.ReportDiagnostic(Diagnostic.Create(RouterClash, declaration.Identifier.GetLocation(),
                    symbol.Name, "not a single base joined it — name the member in [Blobcheg(typeof(...), \"name\")]"));
                return;
            }

            if (router.Dbs.Count > 64)
            {
                source.ReportDiagnostic(Diagnostic.Create(TooManyDomains, declaration.Identifier.GetLocation(),
                    symbol.Name, router.Dbs.Count));
                return;
            }

            var domains = new HashSet<string>();
            var members = new HashSet<string>();
            foreach (var db in router.Dbs)
            {
                if (!domains.Add(db.DomainMetadata))
                {
                    source.ReportDiagnostic(Diagnostic.Create(RouterClash, declaration.Identifier.GetLocation(),
                        symbol.Name, $"domain '{db.DomainMetadata}' joined it twice"));
                    return;
                }

                if (!members.Add(db.Member))
                {
                    source.ReportDiagnostic(Diagnostic.Create(RouterClash, declaration.Identifier.GetLocation(),
                        symbol.Name, $"the member name '{db.Member}' is taken twice"));
                    return;
                }
            }

            var maskWidth = MaskWidthFor(router.Dbs.Count);
            var layoutHash = LayoutHash(router.Dbs, maskWidth);
            var boot = Boot(source, symbol, declaration, model, out var autoCreate);

            var text = new StringBuilder();
            Open(text, symbol, out var space);

            var access = Access(symbol);
            var enumName = symbol.Name + "Db";
            var rowName = symbol.Name + "Row";

            // The bit enum: its width follows the number of bases — the same width the mask has in the file.
            text.AppendLine("    /// <summary>The bases of the router. The bit number is the position of the domain in the sorted list.</summary>");
            text.AppendLine("    [global::System.Flags]");
            text.Append("    ").Append(access).Append(" enum ").Append(enumName).Append(" : ")
                .AppendLine(EnumBase(maskWidth));
            text.AppendLine("    {");
            text.AppendLine("        None = 0,");
            for (var i = 0; i < router.Dbs.Count; i++)
                text.Append("        ").Append(Pascal(router.Dbs[i].Member)).Append(" = 1").Append(i == 0 ? "" : " << " + i).AppendLine(",");
            text.AppendLine("    }");
            text.AppendLine();

            text.AppendLine("    /// <summary>One node across all the bases of the router at once.</summary>");
            text.Append("    ").Append(access).Append(" readonly struct ").AppendLine(rowName);
            text.AppendLine("    {");
            text.AppendLine("        readonly global::Blobcheg.BlobchegRouterRow __row;");
            text.AppendLine();
            text.Append("        internal ").Append(rowName).AppendLine("(global::Blobcheg.BlobchegRouterRow row) => __row = row;");
            text.AppendLine();
            text.Append("        public ").Append(enumName).Append(" Mask => (").Append(enumName).AppendLine(")__row.Mask;");

            for (var i = 0; i < router.Dbs.Count; i++)
            {
                var db = router.Dbs[i];
                text.AppendLine();
                text.Append("        /// <summary>Whether the node has a record in base ").Append(db.DbName).AppendLine(".</summary>");
                text.Append("        public bool Has").Append(Pascal(db.Member)).Append(" => __row.Has(").Append(i).AppendLine(");");
                text.AppendLine();
                text.Append("        /// <summary>The offset of the record in base ").Append(db.DbName)
                    .AppendLine("; if there is no record it throws.</summary>");
                text.Append("        public uint ").Append(db.Member).Append(" => __row.Offset(").Append(i).AppendLine(");");
            }

            text.AppendLine("    }");
            text.AppendLine();

            text.Append("    ").Append(access).Append(" partial struct ").Append(symbol.Name)
                .AppendLine(" : global::System.IDisposable, global::Blobcheg.IBlobchegRouter");
            text.AppendLine("    {");
            text.Append("        public const string RouterName = \"").Append(symbol.Name).AppendLine("\";");
            text.AppendLine();
            text.AppendLine("        /// <summary>The hash of the bit numbering. A file assembled for a different set of bases will not load.</summary>");
            text.Append("        public const ulong LayoutHash = 0x").Append(layoutHash.ToString("X16")).AppendLine("UL;");
            text.AppendLine();
            text.Append("        public const int DomainCount = ").Append(router.Dbs.Count).AppendLine(";");
            text.AppendLine();
            text.AppendLine("        global::Blobcheg.BlobchegRouterBlob __router;");
            text.AppendLine();
            text.Append("        public ").Append(symbol.Name).AppendLine("(global::Blobcheg.BlobchegBuffer buffer)");
            text.AppendLine("        {");
            text.AppendLine("            __router = new global::Blobcheg.BlobchegRouterBlob(buffer, RouterName, DomainCount, LayoutHash);");
            text.AppendLine("        }");
            text.AppendLine();
            text.AppendLine("        /// <summary>The file name of the router — the transport asks for the same one.</summary>");
            text.AppendLine("        public static string FileName => global::Blobcheg.BlobchegNaming.FileName(RouterName);");
            text.AppendLine();
            text.AppendLine("        public string Name => RouterName;");
            text.AppendLine();
            text.AppendLine("        public bool IsCreated => __router.IsCreated;");
            text.AppendLine();
            text.AppendLine("        /// <summary>Rows, that is, nodes. Also the ceiling of the row number in a valid id.</summary>");
            text.AppendLine("        public int Count => __router.Count;");
            text.AppendLine();
            text.AppendLine("        /// <summary>The tag of this router — the high byte of the ids it hands out.</summary>");
            text.AppendLine("        public byte Tag => __router.Tag;");
            text.AppendLine();
            text.AppendLine("        /// <summary>The id of a row by its number — that is how the router is walked whole. Get checks the range.</summary>");
            text.AppendLine("        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
            text.AppendLine("        public global::Blobcheg.BlobchegId IdAt(uint index) => __router.IdAt(index);");
            text.AppendLine();
            text.AppendLine("        /// <summary>The row of a node. An unknown id throws.</summary>");
            text.AppendLine("        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
            text.Append("        public ").Append(rowName).Append(" Get(global::Blobcheg.BlobchegId id) => new ")
                .Append(rowName).AppendLine("(__router.Get(id));");
            text.AppendLine();
            text.AppendLine("        /// <summary>The row of a node without exceptions: an unknown id gives false.</summary>");
            text.Append("        public bool TryGet(global::Blobcheg.BlobchegId id, out ").Append(rowName).AppendLine(" row)");
            text.AppendLine("        {");
            text.AppendLine("            if (!__router.TryGet(id, out var found))");
            text.AppendLine("            {");
            text.AppendLine("                row = default;");
            text.AppendLine("                return false;");
            text.AppendLine("            }");
            text.AppendLine();
            text.Append("            row = new ").Append(rowName).AppendLine("(found);");
            text.AppendLine("            return true;");
            text.AppendLine("        }");

            for (var i = 0; i < router.Dbs.Count; i++)
            {
                var db = router.Dbs[i];
                var pascal = Pascal(db.Member);

                text.AppendLine();
                text.Append("        /// <summary>The offset in base ").Append(db.DbName)
                    .AppendLine(". An unknown id or a missing record throws.</summary>");
                text.AppendLine("        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
                text.Append("        public uint Get").Append(pascal)
                    .Append("(global::Blobcheg.BlobchegId id) => __router.Get(id).Offset(").Append(i).AppendLine(");");
                text.AppendLine();
                text.AppendLine("        /// <summary>The same without exceptions: it never throws.</summary>");
                text.Append("        public bool TryGet").Append(pascal)
                    .AppendLine("(global::Blobcheg.BlobchegId id, out uint offset)");
                text.AppendLine("        {");
                text.AppendLine("            if (!__router.TryGet(id, out var row))");
                text.AppendLine("            {");
                text.AppendLine("                offset = 0;");
                text.AppendLine("                return false;");
                text.AppendLine("            }");
                text.AppendLine();
                text.Append("            return row.TryOffset(").Append(i).AppendLine(", out offset);");
                text.AppendLine("        }");
                text.AppendLine();
                text.Append("        public bool Has").Append(pascal)
                    .Append("(global::Blobcheg.BlobchegId id) => __router.TryGet(id, out var row) && row.Has(")
                    .Append(i).AppendLine(");");
            }

            text.AppendLine();
            text.AppendLine("        public void Dispose() => __router.Dispose();");
            text.AppendLine("    }");

            if (boot)
                EmitBootSystem(text, symbol.Name, access, autoCreate);

            Close(text, space);

            source.AddSource(symbol.Name + ".blobcheg.router.g.cs", SourceText.From(text.ToString(), Encoding.UTF8));
        }

        // ---------------------------------------------------------------- hash table

        /// <summary>
        /// The partial of a hash table. It is declared apart from the router and knows exactly three
        /// constants about it: the name, the number of bases and the hash of the bit numbering. The bit
        /// numbers here are the same as in the router — both are computed from one sorted list of bases.
        /// </summary>
        static void EmitHashes(SourceProductionContext source, GeneratorAttributeSyntaxContext ctx, Model model)
        {
            var symbol = (INamedTypeSymbol)ctx.TargetSymbol;
            var declaration = (StructDeclarationSyntax)ctx.TargetNode;

            if (!declaration.Modifiers.Any(m => m.ValueText == "partial") || symbol.ContainingType != null)
            {
                source.ReportDiagnostic(Diagnostic.Create(HashesNotPartial, declaration.Identifier.GetLocation(), symbol.Name));
                return;
            }

            var attribute = ctx.Attributes[0];
            if (attribute.ConstructorArguments.Length < 1
                || !(attribute.ConstructorArguments[0].Value is INamedTypeSymbol routerSymbol))
                return;

            if (!model.Routers.TryGetValue(routerSymbol.Name, out var router))
            {
                source.ReportDiagnostic(Diagnostic.Create(HashesNoRouter, declaration.Identifier.GetLocation(),
                    symbol.Name, routerSymbol.Name,
                    "it is not marked [BlobchegRouter] in this assembly. A router, its bases and its table " +
                    "are obliged to lie in one assembly: the generator sees only its own compilation"));
                return;
            }

            if (router.Dbs.Count == 0)
            {
                source.ReportDiagnostic(Diagnostic.Create(HashesNoRouter, declaration.Identifier.GetLocation(),
                    symbol.Name, routerSymbol.Name, "not a single base joined it — there is nothing to route"));
                return;
            }

            var maskWidth = MaskWidthFor(router.Dbs.Count);
            var layoutHash = LayoutHash(router.Dbs, maskWidth);
            var boot = Boot(source, symbol, declaration, model, out var autoCreate);

            var text = new StringBuilder();
            Open(text, symbol, out var space);

            var access = Access(symbol);

            text.Append("    ").Append(access).Append(" partial struct ").Append(symbol.Name)
                .AppendLine(" : global::System.IDisposable");
            text.AppendLine("    {");
            text.Append("        public const string RouterName = \"").Append(router.Name).AppendLine("\";");
            text.AppendLine();
            text.AppendLine("        /// <summary>The identity of the table file: the router name plus the suffix.</summary>");
            text.Append("        public const string FileIdentity = \"").Append(router.Name).AppendLine("Hashes\";");
            text.AppendLine();
            text.AppendLine("        /// <summary>The hash of the router bit numbering: the table and the router must be of one build.</summary>");
            text.Append("        public const ulong LayoutHash = 0x").Append(layoutHash.ToString("X16")).AppendLine("UL;");
            text.AppendLine();
            text.Append("        public const int DomainCount = ").Append(router.Dbs.Count).AppendLine(";");
            text.AppendLine();
            text.AppendLine("        global::Blobcheg.BlobchegHashesBlob __hashes;");
            text.AppendLine();
            text.Append("        public ").Append(symbol.Name).AppendLine("(global::Blobcheg.BlobchegBuffer buffer)");
            text.AppendLine("        {");
            text.AppendLine("            __hashes = new global::Blobcheg.BlobchegHashesBlob(");
            text.AppendLine("                buffer, FileIdentity, RouterName, DomainCount, LayoutHash);");
            text.AppendLine("        }");
            text.AppendLine();
            text.AppendLine("        /// <summary>The file name of the table — the transport asks for the same one.</summary>");
            text.AppendLine("        public static string FileName => global::Blobcheg.BlobchegNaming.FileName(FileIdentity);");
            text.AppendLine();
            text.AppendLine("        public bool IsCreated => __hashes.IsCreated;");
            text.AppendLine();
            text.AppendLine("        /// <summary>Rows, that is, nodes of the router, including the holes left by deleted ones.</summary>");
            text.AppendLine("        public int Count => __hashes.Count;");
            text.AppendLine();
            text.AppendLine("        /// <summary>The router tag — the high byte of the ids this table hands out.</summary>");
            text.AppendLine("        public byte Tag => __hashes.Tag;");
            text.AppendLine();
            text.AppendLine("        /// <summary>The id of a node by the hash of its name. An unknown hash throws.</summary>");
            text.AppendLine("        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
            text.AppendLine("        public global::Blobcheg.BlobchegId GetId(ulong hash)");
            text.AppendLine("            => global::Blobcheg.BlobchegId.Make(__hashes.Tag, __hashes.GetRow(hash));");
            text.AppendLine();
            text.AppendLine("        /// <summary>The same without exceptions: there is no node with that name any more — false.</summary>");
            text.AppendLine("        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
            text.AppendLine("        public bool TryGetId(ulong hash, out global::Blobcheg.BlobchegId id)");
            text.AppendLine("        {");
            text.AppendLine("            if (!__hashes.TryGetRow(hash, out var row))");
            text.AppendLine("            {");
            text.AppendLine("                id = default;");
            text.AppendLine("                return false;");
            text.AppendLine("            }");
            text.AppendLine();
            text.AppendLine("            id = global::Blobcheg.BlobchegId.Make(__hashes.Tag, row);");
            text.AppendLine("            return true;");
            text.AppendLine("        }");
            text.AppendLine();
            text.AppendLine("        /// <summary>The hash of a node's name by its id. A hole from a deleted one is zero.</summary>");
            text.AppendLine("        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
            text.AppendLine("        public ulong HashOf(global::Blobcheg.BlobchegId id)");
            text.AppendLine("        {");
            text.AppendLine("            if (id.Tag != __hashes.Tag)");
            text.AppendLine("                throw new global::System.InvalidOperationException(");
            text.AppendLine("                    \"Blobcheg.Hashes: this id was handed out by another router — here it means nothing\");");
            text.AppendLine();
            text.AppendLine("            return __hashes.HashOfRow(id.Index);");
            text.AppendLine("        }");

            for (var i = 0; i < router.Dbs.Count; i++)
            {
                var db = router.Dbs[i];
                var pascal = Pascal(db.Member);

                text.AppendLine();
                text.Append("        /// <summary>The hash by the address of a record in base ").Append(db.DbName)
                    .AppendLine(". If there is no record at that address it throws.</summary>");
                text.Append("        public ulong HashOf").Append(pascal).AppendLine("(uint offset)");
                text.AppendLine("        {");
                text.Append("            if (!__hashes.TryHashOfOffset(").Append(i).AppendLine(", offset, out var hash))");
                text.AppendLine("                throw new global::System.InvalidOperationException(");
                text.AppendLine("                    \"Blobcheg.Hashes: there is no record at that address in that base\");");
                text.AppendLine();
                text.AppendLine("            return hash;");
                text.AppendLine("        }");
                text.AppendLine();
                text.AppendLine("        /// <summary>The same without exceptions: it never throws.</summary>");
                text.Append("        public bool TryHashOf").Append(pascal).AppendLine("(uint offset, out ulong hash)");
                text.Append("            => __hashes.TryHashOfOffset(").Append(i).AppendLine(", offset, out hash);");
            }

            text.AppendLine();
            text.AppendLine("        public void Dispose() => __hashes.Dispose();");
            text.AppendLine("    }");

            // No BlobchegSweep: no entity points into the table buffer, there is nothing to move.
            if (boot)
                EmitBootSystem(text, symbol.Name, access, autoCreate, false);

            Close(text, space);

            source.AddSource(symbol.Name + ".blobcheg.hashes.g.cs", SourceText.From(text.ToString(), Encoding.UTF8));
        }

        // ---------------------------------------------------------------- boot

        /// <summary>
        /// A boot system is emitted for a struct declared <c>IComponentData</c>: that is the explicit
        /// opt-in "I want it as a singleton". Not declared — the load is written by hand, as in v1.
        /// </summary>
        static bool Boot(SourceProductionContext source, INamedTypeSymbol symbol,
            StructDeclarationSyntax declaration, Model model, out bool autoCreate)
        {
            // A [DisableAutoCreation] on the base travels onto the emitted system: "the system is needed,
            // but I decide who creates it". Without that the default world would load a base that has no
            // place in it.
            autoCreate = !symbol.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == DisableAutoCreation);

            var component = symbol.AllInterfaces.Any(i => i.ToDisplayString() == ComponentData);
            if (!component)
                return false;

            if (model.HasEntities)
                return true;

            source.ReportDiagnostic(Diagnostic.Create(NoEntitiesReference, declaration.Identifier.GetLocation(), symbol.Name));
            return false;
        }

        /// <summary>
        /// Only SystemState, EntityManager and EntityQuery: generators do not see each other's output,
        /// so Unity's generator will no longer process a SystemAPI inside an emitted system.
        ///
        /// The system lives in the editor world too (WorldSystemFilterFlags.Editor): without a base any
        /// patch pass there runs into "the domain is not loaded", and subscene entities are always
        /// present in the editor world. For the same reason it does not switch itself off after loading
        /// in the editor but watches the number of its own file: the rebuild rewrote the base — re-read
        /// it and move the slots. In the player everything is as it was, one load and off.
        ///
        /// A transient failure (<c>BlobchegTransientException</c>: the file is not there yet, the read
        /// caught it mid-rewrite) is a warning in the editor rather than an exception: there is nothing
        /// to fix, the rebuild will finish writing the file and the load will run again by itself. In the
        /// player it travels upwards like any other.
        /// </summary>
        static void EmitBootSystem(StringBuilder text, string typeName, string access, bool autoCreate,
            bool sweep = true)
        {
            text.AppendLine();
            text.Append("    /// <summary>Loading '").Append(typeName).AppendLine("' into a singleton. Emitted by the codegen.</summary>");
            text.AppendLine("    [global::Unity.Entities.WorldSystemFilter(");
            text.AppendLine("        global::Unity.Entities.WorldSystemFilterFlags.Default | global::Unity.Entities.WorldSystemFilterFlags.Editor)]");
            text.AppendLine("    [global::Unity.Entities.UpdateInGroup(typeof(global::Blobcheg.BlobchegBootGroup))]");
            if (!autoCreate)
                text.AppendLine("    [global::Unity.Entities.DisableAutoCreation]");
            text.Append("    ").Append(access).Append(" partial struct ").Append(typeName)
                .AppendLine("BootSystem : global::Unity.Entities.ISystem");
            text.AppendLine("    {");
            text.AppendLine("        global::Blobcheg.BlobchegLoad __load;");
            text.AppendLine("        global::Unity.Entities.EntityQuery __query;");
            text.AppendLine("        bool __created;");
            text.AppendLine("#if UNITY_EDITOR");
            text.AppendLine("        int __seen;");
            text.AppendLine("        bool __broken;");
            text.AppendLine("        bool __quiet;");
            text.AppendLine("#endif");
            text.AppendLine();
            text.AppendLine("        public void OnCreate(ref global::Unity.Entities.SystemState state)");
            text.AppendLine("        {");
            text.Append("            __load = global::Blobcheg.BlobchegTransport.Default.Read(").Append(typeName)
                .AppendLine(".FileName, global::Unity.Collections.Allocator.Persistent);");
            // A write query and not a read one: the reload puts the new blob into the singleton with it.
            text.Append("            __query = state.GetEntityQuery(global::Unity.Entities.ComponentType.ReadWrite<")
                .Append(typeName).AppendLine(">());");
            text.AppendLine("#if UNITY_EDITOR");
            text.AppendLine("            // The file number is taken at the START of the read and not at its end: the rebuild that");
            text.AppendLine("            // landed in the middle of the read is the one that broke it, and its number is obliged to");
            text.AppendLine("            // stay unseen, otherwise there is nothing left to re-read and the world freezes on the old one.");
            text.Append("            __seen = global::Blobcheg.BlobchegFileVersions.Of(").Append(typeName)
                .AppendLine(".FileName);");
            text.AppendLine("#endif");
            text.AppendLine("        }");
            text.AppendLine();
            text.AppendLine("        public void OnUpdate(ref global::Unity.Entities.SystemState state)");
            text.AppendLine("        {");
            text.AppendLine("#if UNITY_EDITOR");
            text.AppendLine("            if (__created)");
            text.AppendLine("            {");
            text.AppendLine("                __Reraise(ref state);");
            text.AppendLine("                return;");
            text.AppendLine("            }");
            text.AppendLine();
            text.AppendLine("            // The load already broke — the file is bad. We wait for the rebuild to rewrite it: without");
            text.AppendLine("            // that the same failure would repeat every frame and the repaired file would never reach the world.");
            text.AppendLine("            if (__broken)");
            text.AppendLine("            {");
            text.Append("                if (!global::Blobcheg.BlobchegFileVersions.Changed(").Append(typeName)
                .AppendLine(".FileName, ref __seen))");
            text.AppendLine("                    return;");
            text.AppendLine();
            text.Append("                __load = global::Blobcheg.BlobchegTransport.Default.Read(").Append(typeName)
                .AppendLine(".FileName, global::Unity.Collections.Allocator.Persistent);");
            text.AppendLine("                __broken = false;");
            text.AppendLine("            }");
            text.AppendLine("#endif");
            text.AppendLine();
            text.AppendLine("            bool __ready;");
            text.AppendLine("            try");
            text.AppendLine("            {");
            text.AppendLine("                __ready = __load.Poll();");
            text.AppendLine("            }");
            text.AppendLine("#if UNITY_EDITOR");
            text.AppendLine("            catch (global::Blobcheg.BlobchegTransientException __transient)");
            text.AppendLine("            {");
            text.AppendLine("                __Broke(ref state);");
            text.AppendLine("                __Notify(__transient);");
            text.AppendLine("                return;");
            text.AppendLine("            }");
            text.AppendLine("#endif");
            text.AppendLine("            catch");
            text.AppendLine("            {");
            text.AppendLine("                __Broke(ref state);");
            text.AppendLine("                throw;");
            text.AppendLine("            }");
            text.AppendLine();
            text.AppendLine("            if (!__ready)");
            text.AppendLine("                return;");
            text.AppendLine();
            text.AppendLine("            // Ownership of the buffer has left the read: if the constructor rejects the file, there is");
            text.AppendLine("            // nobody left to free the buffer, and every load attempt would leak a whole base.");
            text.AppendLine("            var __buffer = __load.Acquire();");
            text.Append("            ").Append(typeName).AppendLine(" __value;");
            text.AppendLine("            try");
            text.AppendLine("            {");
            text.Append("                __value = new ").Append(typeName).AppendLine("(__buffer);");
            text.AppendLine("            }");
            text.AppendLine("#if UNITY_EDITOR");
            text.AppendLine("            catch (global::Blobcheg.BlobchegTransientException __transient)");
            text.AppendLine("            {");
            text.AppendLine("                __buffer.Dispose();");
            text.AppendLine("                __Broke(ref state);");
            text.AppendLine("                __Notify(__transient);");
            text.AppendLine("                return;");
            text.AppendLine("            }");
            text.AppendLine("#endif");
            text.AppendLine("            catch");
            text.AppendLine("            {");
            text.AppendLine("                __buffer.Dispose();");
            text.AppendLine("                __Broke(ref state);");
            text.AppendLine("                throw;");
            text.AppendLine("            }");
            text.AppendLine();
            text.AppendLine("            state.EntityManager.CreateSingleton(__value);");
            text.AppendLine("            __created = true;");
            text.AppendLine("#if UNITY_EDITOR");
            text.AppendLine("            __quiet = false;");
            if (sweep)
            {
                text.AppendLine();
                text.AppendLine("            // The entities that arrived before the base are waiting for exactly this pass: their slots");
                text.AppendLine("            // stayed offsets, and now there is somewhere to translate them onto.");
                text.AppendLine("            global::Blobcheg.BlobchegSweep.Run(state.EntityManager);");
            }

            text.AppendLine("#else");
            text.AppendLine("            state.Enabled = false;");
            text.AppendLine("#endif");
            text.AppendLine("        }");
            text.AppendLine();
            text.AppendLine("        /// <summary>");
            text.AppendLine("        /// The load broke. A real failure travels upwards once, and the read ends right here:");
            text.AppendLine("        /// repeating it every frame is drowning in a retelling of the consequences.");
            text.AppendLine("        /// In the player the file will not be repaired any more, so the system switches off; in the");
            text.AppendLine("        /// editor it waits for the rebuild that will rewrite the file.");
            text.AppendLine("        /// </summary>");
            text.AppendLine("        void __Broke(ref global::Unity.Entities.SystemState state)");
            text.AppendLine("        {");
            text.AppendLine("            __load.Dispose();");
            text.AppendLine("#if UNITY_EDITOR");
            text.AppendLine("            // The file number is not touched here: it was taken at the start of the read, and the");
            text.AppendLine("            // rebuild that broke that read is obliged to stay unseen — otherwise there is nothing to repair with.");
            text.AppendLine("            __broken = true;");
            text.AppendLine("#else");
            text.AppendLine("            state.Enabled = false;");
            text.AppendLine("#endif");
            text.AppendLine("        }");
            text.AppendLine();
            text.AppendLine("#if UNITY_EDITOR");
            text.AppendLine("        /// <summary>");
            text.AppendLine("        /// The rebuild rewrote the base file — re-read it into the live world. The order here is not");
            text.AppendLine("        /// a matter of taste: the new buffer is registered first so that the previous one moves into the");
            text.AppendLine("        /// retired generations, and only through those do slots with old addresses reach the new ones.");
            text.AppendLine("        /// </summary>");
            text.AppendLine("        void __Reraise(ref global::Unity.Entities.SystemState state)");
            text.AppendLine("        {");
            text.AppendLine("            var __was = __seen;");
            text.Append("            if (!global::Blobcheg.BlobchegFileVersions.Changed(").Append(typeName)
                .AppendLine(".FileName, ref __seen))");
            text.AppendLine("                return;");
            text.AppendLine();
            text.Append("            var reload = global::Blobcheg.BlobchegTransport.Default.Read(").Append(typeName)
                .AppendLine(".FileName, global::Unity.Collections.Allocator.Persistent);");
            text.AppendLine("            try");
            text.AppendLine("            {");
            text.AppendLine("                // Waiting for the file is allowed in the editor: this is a local disk, not StreamingAssets in an APK.");
            text.AppendLine("                reload.Complete();");
            text.AppendLine("            }");
            text.AppendLine("            catch (global::Blobcheg.BlobchegTransientException __transient)");
            text.AppendLine("            {");
            text.AppendLine("                reload.Dispose();");
            text.AppendLine("                __Retry(__transient, __was);");
            text.AppendLine("                return;");
            text.AppendLine("            }");
            text.AppendLine("            catch");
            text.AppendLine("            {");
            text.AppendLine("                reload.Dispose();");
            text.AppendLine("                throw;");
            text.AppendLine("            }");
            text.AppendLine();
            text.AppendLine("            var buffer = reload.Acquire();");
            text.Append("            ").Append(typeName).AppendLine(" fresh;");
            text.AppendLine("            try");
            text.AppendLine("            {");
            text.Append("                fresh = new ").Append(typeName).AppendLine("(buffer);");
            text.AppendLine("            }");
            text.AppendLine("            catch (global::Blobcheg.BlobchegTransientException __transient)");
            text.AppendLine("            {");
            text.AppendLine("                buffer.Dispose();");
            text.AppendLine("                __Retry(__transient, __was);");
            text.AppendLine("                return;");
            text.AppendLine("            }");
            text.AppendLine("            catch");
            text.AppendLine("            {");
            text.AppendLine("                // Ownership has already left the read: not freeing the buffer here means leaking a base,");
            text.AppendLine("                // while the previous one in the singleton is still whole and the world keeps running on it.");
            text.AppendLine("                buffer.Dispose();");
            text.AppendLine("                throw;");
            text.AppendLine("            }");
            text.AppendLine();
            text.AppendLine("            // The jobs reading the previous buffer are obliged to finish before it is freed.");
            text.AppendLine("            state.EntityManager.CompleteAllTrackedJobs();");
            text.AppendLine();
            text.Append("            var stale = __query.GetSingleton<").Append(typeName).AppendLine(">();");
            text.AppendLine("            stale.Dispose();");
            text.AppendLine();
            text.AppendLine("            __query.SetSingleton(fresh);");
            text.AppendLine("            __quiet = false;");

            if (sweep)
                text.AppendLine("            global::Blobcheg.BlobchegSweep.Run(state.EntityManager);");

            text.AppendLine("        }");
            text.AppendLine();
            text.AppendLine("        /// <summary>");
            text.AppendLine("        /// The reload caught a transient moment. The file number is put back — otherwise there would");
            text.AppendLine("        /// be nothing to re-read until the next rebuild, and the world would silently stay on the old");
            text.AppendLine("        /// base. The warning is one per streak: the file is being finished over a frame or two, and");
            text.AppendLine("        /// retelling that every frame is the same flood that saying it once saves from.");
            text.AppendLine("        /// </summary>");
            text.AppendLine("        void __Retry(global::Blobcheg.BlobchegTransientException __transient, int __was)");
            text.AppendLine("        {");
            text.AppendLine("            __seen = __was;");
            text.AppendLine("            if (__quiet)");
            text.AppendLine("                return;");
            text.AppendLine();
            text.AppendLine("            __quiet = true;");
            text.AppendLine("            __Notify(__transient);");
            text.AppendLine("        }");
            text.AppendLine();
            text.AppendLine("        /// <summary>");
            text.AppendLine("        /// A transient failure in the editor is a notification and not a problem: there is nothing to");
            text.AppendLine("        /// fix, the rebuild will finish the file. A red error here lies about a breakage that is not there.");
            text.AppendLine("        /// </summary>");
            text.AppendLine("        static void __Notify(global::Blobcheg.BlobchegTransientException __transient)");
            text.AppendLine("        {");
            text.AppendLine("            global::UnityEngine.Debug.LogWarning(__transient.Message +");
            text.AppendLine("                \" — this is a notification and not a problem: in the editor the moment is transient, the base\" +");
            text.AppendLine("                \" will load by itself as soon as the rebuild rewrites the file.\");");
            text.AppendLine("        }");
            text.AppendLine("#endif");
            text.AppendLine();
            text.AppendLine("        public void OnDestroy(ref global::Unity.Entities.SystemState state)");
            text.AppendLine("        {");
            text.AppendLine("            if (__created)");
            text.AppendLine("            {");
            text.Append("                var value = __query.GetSingleton<").Append(typeName).AppendLine(">();");
            text.AppendLine("                value.Dispose();");
            text.AppendLine("            }");
            text.AppendLine("            else");
            text.AppendLine("            {");
            text.AppendLine("                __load.Dispose();");
            text.AppendLine("            }");
            text.AppendLine("        }");
            text.AppendLine("    }");
        }

        // ---------------------------------------------------------------- odds and ends

        static void Open(StringBuilder text, INamedTypeSymbol symbol, out string space)
        {
            space = symbol.ContainingNamespace.IsGlobalNamespace ? null : symbol.ContainingNamespace.ToDisplayString();

            text.AppendLine("// <auto-generated/> Blobcheg");
            text.AppendLine("#pragma warning disable");

            if (space == null)
                return;

            text.Append("namespace ").AppendLine(space);
            text.AppendLine("{");
        }

        static void Close(StringBuilder text, string space)
        {
            if (space != null)
                text.AppendLine("}");
        }

        static string Access(INamedTypeSymbol symbol)
            => symbol.DeclaredAccessibility == Accessibility.Public ? "public" : "internal";

        static string EnumBase(int maskWidth)
        {
            switch (maskWidth)
            {
                case 1: return "byte";
                case 2: return "ushort";
                case 4: return "uint";
                default: return "ulong";
            }
        }

        static int MaskWidthFor(int domainCount)
        {
            if (domainCount <= 8)
                return 1;
            if (domainCount <= 16)
                return 2;
            if (domainCount <= 32)
                return 4;
            return 8;
        }

        static string Pascal(string member)
        {
            if (string.IsNullOrEmpty(member))
                return member;

            return char.ToUpperInvariant(member[0]) + member.Substring(1);
        }

        /// <summary>The name as reflection sees it: nested ones through '+'. The editor computes the hash from the same one.</summary>
        static string MetadataName(INamedTypeSymbol symbol)
        {
            var name = symbol.MetadataName;

            for (var owner = symbol.ContainingType; owner != null; owner = owner.ContainingType)
                name = owner.MetadataName + "+" + name;

            return symbol.ContainingNamespace.IsGlobalNamespace
                ? name
                : symbol.ContainingNamespace.ToDisplayString() + "." + name;
        }

        /// <summary>
        /// fnv1a-64 over the domains and the member names in bit order plus the mask width. Duplicated in
        /// <c>BlobchegRouterFormat.LayoutHash</c> — change only in pairs, the whole agreement of the bit
        /// numbering between the codegen and the editor stands on this.
        /// </summary>
        static ulong LayoutHash(IReadOnlyList<DbInfo> dbs, int maskWidth)
        {
            const ulong prime = 1099511628211;
            var hash = 14695981039346656037;

            foreach (var db in dbs)
            {
                Feed(db.DomainMetadata);
                Feed("\n");
                Feed(db.Member);
                Feed("\n");
            }

            hash ^= (byte)maskWidth;
            hash *= prime;
            return hash;

            void Feed(string value)
            {
                foreach (var b in Encoding.UTF8.GetBytes(value ?? string.Empty))
                {
                    hash ^= b;
                    hash *= prime;
                }
            }
        }
    }
}
