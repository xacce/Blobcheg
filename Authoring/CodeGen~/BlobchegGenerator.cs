using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Blobcheg.CodeGen
{
    /// <summary>
    /// Дописывает партиал базы по <c>[Blobcheg(typeof(IДомен))]</c> и партиал роутера по
    /// <c>[BlobchegRouter]</c>. Генератор выпускает ТОЛЬКО структуры и системы: ScriptableObject он
    /// выпускать не может — у типа из кодогена нет MonoScript, и ассетом такой тип не станет.
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
            "BCHG001", "База обязана быть partial",
            "Структура '{0}' помечена [Blobcheg], но не объявлена partial — дописать в неё нечего",
            "Blobcheg", DiagnosticSeverity.Error, true);

        static readonly DiagnosticDescriptor Nested = new DiagnosticDescriptor(
            "BCHG002", "База не может быть вложенным типом",
            "Структура '{0}' помечена [Blobcheg], но вложена в другой тип — вынеси её наружу",
            "Blobcheg", DiagnosticSeverity.Error, true);

        static readonly DiagnosticDescriptor BadDomain = new DiagnosticDescriptor(
            "BCHG003", "Домен обязан быть интерфейсом-маркером",
            "В [Blobcheg] у структуры '{0}' передан '{1}' — домен объявляется интерфейсом",
            "Blobcheg", DiagnosticSeverity.Error, true);

        static readonly DiagnosticDescriptor NoRouter = new DiagnosticDescriptor(
            "BCHG004", "Роутер не определён",
            "У структуры '{0}' задано имя члена роутера, но роутер не выбран: {1}. " +
            "Укажи Router = typeof(...) в [Blobcheg]",
            "Blobcheg", DiagnosticSeverity.Error, true);

        static readonly DiagnosticDescriptor RouterNotPartial = new DiagnosticDescriptor(
            "BCHG005", "Роутер обязан быть partial и не вложенным",
            "Структура '{0}' помечена [BlobchegRouter], но не объявлена partial или вложена в другой тип",
            "Blobcheg", DiagnosticSeverity.Error, true);

        static readonly DiagnosticDescriptor RouterClash = new DiagnosticDescriptor(
            "BCHG006", "Роутер собран из противоречивых баз",
            "Роутер '{0}': {1}",
            "Blobcheg", DiagnosticSeverity.Error, true);

        static readonly DiagnosticDescriptor TooManyDomains = new DiagnosticDescriptor(
            "BCHG007", "В роутере больше 64 баз",
            "Роутер '{0}' собран из {1} баз — потолок 64. Это не «мало бит», а неправильно нарезанный проект",
            "Blobcheg", DiagnosticSeverity.Error, true);

        static readonly DiagnosticDescriptor NoEntitiesReference = new DiagnosticDescriptor(
            "BCHG008", "Нет ссылки на Blobcheg.Entities",
            "Структура '{0}' объявлена IComponentData — под неё выпускается бут-система, а сборка не " +
            "референсит Blobcheg.Entities. Добавь ссылку или убери IComponentData",
            "Blobcheg", DiagnosticSeverity.Error, true);

        static readonly DiagnosticDescriptor HashesNotPartial = new DiagnosticDescriptor(
            "BCHG009", "Таблица хешей обязана быть partial и не вложенной",
            "Структура '{0}' помечена [BlobchegHashes], но не объявлена partial или вложена в другой тип",
            "Blobcheg", DiagnosticSeverity.Error, true);

        static readonly DiagnosticDescriptor HashesNoRouter = new DiagnosticDescriptor(
            "BCHG010", "Таблица хешей ссылается не на роутер",
            "В [BlobchegHashes] у структуры '{0}' передан '{1}': {2}",
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

        // ---------------------------------------------------------------- модель

        sealed class DbInfo
        {
            public string DbName;
            public string DomainMetadata;
            public string Member;
            public string RouterName;

            /// <summary>Роутер назван, но в этой сборке его нет — значит он в чужой.</summary>
            public string RouterElsewhere;
        }

        sealed class RouterInfo
        {
            public string Name;
            public readonly List<DbInfo> Dbs = new List<DbInfo>();
        }

        /// <summary>
        /// Базы и роутеры ЭТОЙ сборки. Чужие сборки не смотрим намеренно: генератор роутера считает
        /// биты по списку баз, а видит он только свою компиляцию — база из сборки, которая ссылается
        /// на роутер, ему не видна, и биты вышли бы посчитанными не по всем.
        ///
        /// Отсюда правило: роутер и его базы лежат в одной сборке. Обратный порядок ссылок делу не
        /// помогает — база, видящая роутер, роутеру не видна by construction.
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

        // ---------------------------------------------------------------- базы

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
                        ? $"роутер '{info.RouterElsewhere}' лежит в другой сборке, а роутер и его базы обязаны быть в одной"
                        : model.Routers.Count == 0
                            ? "в этой сборке нет ни одного [BlobchegRouter]"
                            : $"роутеров в сборке сразу {model.Routers.Count}"));
                return;
            }

            var boot = Boot(source, symbol, declaration, model, out var autoCreate);

            var text = new StringBuilder();
            var domainFull = domain.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            Open(text, symbol, out var space);

            // Без unsafe: тип с указателем внутри держится полем и в безопасном коде, а требовать
            // от потребителя allowUnsafeCode ради объявления базы незачем.
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
            text.AppendLine("        /// <summary>Имя файла базы — его же спрашивает транспорт.</summary>");
            text.AppendLine("        public static string FileName => global::Blobcheg.BlobchegNaming.FileName(DomainName);");
            text.AppendLine();
            text.AppendLine("        public bool IsCreated => __blob.IsCreated;");
            text.AppendLine();
            text.AppendLine("        public int Length => __blob.Length;");
            text.AppendLine();
            text.AppendLine("        /// <summary>Есть ли в файле отладочный контур. В релизном плеере его не бывает.</summary>");
            text.AppendLine("        public bool HasDebug => __blob.HasDebug;");
            text.AppendLine();
            text.AppendLine("        /// <summary>Имена типа и ноды по оффсету — только для инструментов едитора.</summary>");
            text.AppendLine("        public void Describe(uint offset, out string typeName, out string nodeName)");
            text.AppendLine("            => __blob.Describe(offset, out typeName, out nodeName);");
            text.AppendLine();
            text.AppendLine("        /// <summary>Чужой домен здесь не компилируется — это единственная проверка, работающая всегда.</summary>");
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

        // ---------------------------------------------------------------- роутер

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
                    symbol.Name, "ни одна база в него не вступила — назови член в [Blobcheg(typeof(...), \"имя\")]"));
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
                        symbol.Name, $"домен '{db.DomainMetadata}' вступил в него дважды"));
                    return;
                }

                if (!members.Add(db.Member))
                {
                    source.ReportDiagnostic(Diagnostic.Create(RouterClash, declaration.Identifier.GetLocation(),
                        symbol.Name, $"имя члена '{db.Member}' занято дважды"));
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

            // enum бит: ширина по числу баз — она же ширина маски в файле.
            text.AppendLine("    /// <summary>Базы роутера. Номер бита — позиция домена в отсортированном списке.</summary>");
            text.AppendLine("    [global::System.Flags]");
            text.Append("    ").Append(access).Append(" enum ").Append(enumName).Append(" : ")
                .AppendLine(EnumBase(maskWidth));
            text.AppendLine("    {");
            text.AppendLine("        None = 0,");
            for (var i = 0; i < router.Dbs.Count; i++)
                text.Append("        ").Append(Pascal(router.Dbs[i].Member)).Append(" = 1").Append(i == 0 ? "" : " << " + i).AppendLine(",");
            text.AppendLine("    }");
            text.AppendLine();

            text.AppendLine("    /// <summary>Одна нода во всех базах роутера сразу.</summary>");
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
                text.Append("        /// <summary>Есть ли запись ноды в базе ").Append(db.DbName).AppendLine(".</summary>");
                text.Append("        public bool Has").Append(Pascal(db.Member)).Append(" => __row.Has(").Append(i).AppendLine(");");
                text.AppendLine();
                text.Append("        /// <summary>Оффсет записи в базе ").Append(db.DbName)
                    .AppendLine("; записи нет — бросает.</summary>");
                text.Append("        public uint ").Append(db.Member).Append(" => __row.Offset(").Append(i).AppendLine(");");
            }

            text.AppendLine("    }");
            text.AppendLine();

            text.Append("    ").Append(access).Append(" partial struct ").Append(symbol.Name)
                .AppendLine(" : global::System.IDisposable, global::Blobcheg.IBlobchegRouter");
            text.AppendLine("    {");
            text.Append("        public const string RouterName = \"").Append(symbol.Name).AppendLine("\";");
            text.AppendLine();
            text.AppendLine("        /// <summary>Хеш нумерации бит. Файл, собранный под другой набор баз, не поднимется.</summary>");
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
            text.AppendLine("        /// <summary>Имя файла роутера — его же спрашивает транспорт.</summary>");
            text.AppendLine("        public static string FileName => global::Blobcheg.BlobchegNaming.FileName(RouterName);");
            text.AppendLine();
            text.AppendLine("        public string Name => RouterName;");
            text.AppendLine();
            text.AppendLine("        public bool IsCreated => __router.IsCreated;");
            text.AppendLine();
            text.AppendLine("        /// <summary>Строк, то есть нод. Он же потолок номера строки в валидном id.</summary>");
            text.AppendLine("        public int Count => __router.Count;");
            text.AppendLine();
            text.AppendLine("        /// <summary>Тег этого роутера — старший байт id, которые он раздаёт.</summary>");
            text.AppendLine("        public byte Tag => __router.Tag;");
            text.AppendLine();
            text.AppendLine("        /// <summary>Id строки по её номеру — так роутер обходят целиком. Диапазон проверяет Get.</summary>");
            text.AppendLine("        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
            text.AppendLine("        public global::Blobcheg.BlobchegId IdAt(uint index) => __router.IdAt(index);");
            text.AppendLine();
            text.AppendLine("        /// <summary>Строка ноды. Неизвестный id — бросает.</summary>");
            text.AppendLine("        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
            text.Append("        public ").Append(rowName).Append(" Get(global::Blobcheg.BlobchegId id) => new ")
                .Append(rowName).AppendLine("(__router.Get(id));");
            text.AppendLine();
            text.AppendLine("        /// <summary>Строка ноды без исключений: неизвестный id — false.</summary>");
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
                text.Append("        /// <summary>Оффсет в базе ").Append(db.DbName)
                    .AppendLine(". Неизвестный id или отсутствие записи — бросает.</summary>");
                text.AppendLine("        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
                text.Append("        public uint Get").Append(pascal)
                    .Append("(global::Blobcheg.BlobchegId id) => __router.Get(id).Offset(").Append(i).AppendLine(");");
                text.AppendLine();
                text.AppendLine("        /// <summary>То же без исключений: не бросает никогда.</summary>");
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

        // ---------------------------------------------------------------- таблица хешей

        /// <summary>
        /// Партиал таблицы хешей. Она объявляется отдельно от роутера и знает о нём ровно три
        /// константы: имя, число баз и хеш нумерации бит. Номера бит здесь те же, что у роутера, —
        /// оба считаются из одного отсортированного списка баз.
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
                    "он не помечен [BlobchegRouter] в этой сборке. Роутер, его базы и его таблица " +
                    "обязаны лежать в одной сборке: генератор видит только свою компиляцию"));
                return;
            }

            if (router.Dbs.Count == 0)
            {
                source.ReportDiagnostic(Diagnostic.Create(HashesNoRouter, declaration.Identifier.GetLocation(),
                    symbol.Name, routerSymbol.Name, "в него не вступила ни одна база — маршрутизировать нечего"));
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
            text.AppendLine("        /// <summary>Личность файла таблицы: имя роутера плюс суффикс.</summary>");
            text.Append("        public const string FileIdentity = \"").Append(router.Name).AppendLine("Hashes\";");
            text.AppendLine();
            text.AppendLine("        /// <summary>Хеш нумерации бит роутера: таблица и роутер обязаны быть одной сборки.</summary>");
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
            text.AppendLine("        /// <summary>Имя файла таблицы — его же спрашивает транспорт.</summary>");
            text.AppendLine("        public static string FileName => global::Blobcheg.BlobchegNaming.FileName(FileIdentity);");
            text.AppendLine();
            text.AppendLine("        public bool IsCreated => __hashes.IsCreated;");
            text.AppendLine();
            text.AppendLine("        /// <summary>Строк, то есть нод роутера, включая дырки от удалённых.</summary>");
            text.AppendLine("        public int Count => __hashes.Count;");
            text.AppendLine();
            text.AppendLine("        /// <summary>Тег роутера — старший байт id, которые отдаёт эта таблица.</summary>");
            text.AppendLine("        public byte Tag => __hashes.Tag;");
            text.AppendLine();
            text.AppendLine("        /// <summary>Id ноды по хешу её имени. Неизвестный хеш — бросает.</summary>");
            text.AppendLine("        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
            text.AppendLine("        public global::Blobcheg.BlobchegId GetId(ulong hash)");
            text.AppendLine("            => global::Blobcheg.BlobchegId.Make(__hashes.Tag, __hashes.GetRow(hash));");
            text.AppendLine();
            text.AppendLine("        /// <summary>То же без исключений: ноды с таким именем больше нет — false.</summary>");
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
            text.AppendLine("        /// <summary>Хеш имени ноды по её id. Дырка от удалённой — ноль.</summary>");
            text.AppendLine("        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
            text.AppendLine("        public ulong HashOf(global::Blobcheg.BlobchegId id)");
            text.AppendLine("        {");
            text.AppendLine("            if (id.Tag != __hashes.Tag)");
            text.AppendLine("                throw new global::System.InvalidOperationException(");
            text.AppendLine("                    \"Blobcheg.Hashes: этот id выдан другим роутером — здесь он не значит ничего\");");
            text.AppendLine();
            text.AppendLine("            return __hashes.HashOfRow(id.Index);");
            text.AppendLine("        }");

            for (var i = 0; i < router.Dbs.Count; i++)
            {
                var db = router.Dbs[i];
                var pascal = Pascal(db.Member);

                text.AppendLine();
                text.Append("        /// <summary>Хеш по адресу записи в базе ").Append(db.DbName)
                    .AppendLine(". Записи по этому адресу нет — бросает.</summary>");
                text.Append("        public ulong HashOf").Append(pascal).AppendLine("(uint offset)");
                text.AppendLine("        {");
                text.Append("            if (!__hashes.TryHashOfOffset(").Append(i).AppendLine(", offset, out var hash))");
                text.AppendLine("                throw new global::System.InvalidOperationException(");
                text.AppendLine("                    \"Blobcheg.Hashes: по этому адресу в этой базе записи нет\");");
                text.AppendLine();
                text.AppendLine("            return hash;");
                text.AppendLine("        }");
                text.AppendLine();
                text.AppendLine("        /// <summary>То же без исключений: не бросает никогда.</summary>");
                text.Append("        public bool TryHashOf").Append(pascal).AppendLine("(uint offset, out ulong hash)");
                text.Append("            => __hashes.TryHashOfOffset(").Append(i).AppendLine(", offset, out hash);");
            }

            text.AppendLine();
            text.AppendLine("        public void Dispose() => __hashes.Dispose();");
            text.AppendLine("    }");

            // Без BlobchegSweep: в буфер таблицы никто из сущностей не указывает, переселять нечего.
            if (boot)
                EmitBootSystem(text, symbol.Name, access, autoCreate, false);

            Close(text, space);

            source.AddSource(symbol.Name + ".blobcheg.hashes.g.cs", SourceText.From(text.ToString(), Encoding.UTF8));
        }

        // ---------------------------------------------------------------- бут

        /// <summary>
        /// Бут-система выпускается на структуру, объявленную <c>IComponentData</c>: это и есть явный
        /// опт-ин «хочу её синглтоном». Не объявлена — подъём пишется руками, как в v1.
        /// </summary>
        static bool Boot(SourceProductionContext source, INamedTypeSymbol symbol,
            StructDeclarationSyntax declaration, Model model, out bool autoCreate)
        {
            // [DisableAutoCreation] на базе едет на выпущенную систему: «система нужна, но кто её
            // создаёт — решаю я». Без этого дефолтный мир поднимал бы базу, которой в нём не место.
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
        /// Только SystemState, EntityManager и EntityQuery: генераторы не видят выход друг друга,
        /// поэтому SystemAPI в выпущенной системе Unity'шный генератор уже не обработает.
        ///
        /// Система живёт и в редакторном мире (WorldSystemFilterFlags.Editor): без базы там любой
        /// проход патча упирается в «домен не поднят», а сущности сабсцен в редакторном мире есть
        /// всегда. Из-за этого же она в редакторе не гаснет после подъёма, а сторожит номер своего
        /// файла: пересборка переписала базу — перечитать и переселить слоты. В плеере всё как было,
        /// один подъём и выключение.
        /// </summary>
        static void EmitBootSystem(StringBuilder text, string typeName, string access, bool autoCreate,
            bool sweep = true)
        {
            text.AppendLine();
            text.Append("    /// <summary>Подъём '").Append(typeName).AppendLine("' в синглтон. Выпущен кодогеном.</summary>");
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
            text.AppendLine("#endif");
            text.AppendLine();
            text.AppendLine("        public void OnCreate(ref global::Unity.Entities.SystemState state)");
            text.AppendLine("        {");
            text.Append("            __load = global::Blobcheg.BlobchegTransport.Default.Read(").Append(typeName)
                .AppendLine(".FileName, global::Unity.Collections.Allocator.Persistent);");
            // Запрос на запись, а не на чтение: перезаливка кладёт им же новый блоб в синглтон.
            text.Append("            __query = state.GetEntityQuery(global::Unity.Entities.ComponentType.ReadWrite<")
                .Append(typeName).AppendLine(">());");
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
            text.AppendLine("            // Подъём уже срывался — файл битый. Ждём, пока пересборка перепишет его: без этого");
            text.AppendLine("            // тот же отказ повторялся бы каждый кадр, а починенный файл не доехал бы до мира.");
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
            text.AppendLine("            catch");
            text.AppendLine("            {");
            text.AppendLine("                __Broke(ref state);");
            text.AppendLine("                throw;");
            text.AppendLine("            }");
            text.AppendLine();
            text.AppendLine("            if (!__ready)");
            text.AppendLine("                return;");
            text.AppendLine();
            text.AppendLine("            // Владение буфером ушло из чтения: отобьёт файл конструктор — освободить буфер");
            text.AppendLine("            // больше некому, и каждая попытка подъёма утекала бы целой базой.");
            text.AppendLine("            var __buffer = __load.Acquire();");
            text.Append("            ").Append(typeName).AppendLine(" __value;");
            text.AppendLine("            try");
            text.AppendLine("            {");
            text.Append("                __value = new ").Append(typeName).AppendLine("(__buffer);");
            text.AppendLine("            }");
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
            text.Append("            __seen = global::Blobcheg.BlobchegFileVersions.Of(").Append(typeName)
                .AppendLine(".FileName);");
            if (sweep)
            {
                text.AppendLine();
                text.AppendLine("            // Сущности, приехавшие раньше базы, ждут ровно этого прохода: их слоты остались");
                text.AppendLine("            // оффсетами, и теперь есть куда их переводить.");
                text.AppendLine("            global::Blobcheg.BlobchegSweep.Run(state.EntityManager);");
            }

            text.AppendLine("#else");
            text.AppendLine("            state.Enabled = false;");
            text.AppendLine("#endif");
            text.AppendLine("        }");
            text.AppendLine();
            text.AppendLine("        /// <summary>");
            text.AppendLine("        /// Подъём сорвался. Настоящий отказ уезжает наверх один раз, а чтение здесь и");
            text.AppendLine("        /// кончается: повторять его каждым кадром — это тонуть в пересказе последствий.");
            text.AppendLine("        /// В плеере файл больше не починится, поэтому система гаснет; в редакторе она ждёт");
            text.AppendLine("        /// пересборки, которая перепишет файл.");
            text.AppendLine("        /// </summary>");
            text.AppendLine("        void __Broke(ref global::Unity.Entities.SystemState state)");
            text.AppendLine("        {");
            text.AppendLine("            __load.Dispose();");
            text.AppendLine("#if UNITY_EDITOR");
            text.AppendLine("            __broken = true;");
            text.Append("            __seen = global::Blobcheg.BlobchegFileVersions.Of(").Append(typeName)
                .AppendLine(".FileName);");
            text.AppendLine("#else");
            text.AppendLine("            state.Enabled = false;");
            text.AppendLine("#endif");
            text.AppendLine("        }");
            text.AppendLine();
            text.AppendLine("#if UNITY_EDITOR");
            text.AppendLine("        /// <summary>");
            text.AppendLine("        /// Пересборка переписала файл базы — перечитать его в живой мир. Порядок здесь не");
            text.AppendLine("        /// вкусовой: новый буфер регистрируется первым, чтобы прежний ушёл в отставные");
            text.AppendLine("        /// поколения, и только по ним слоты со старыми адресами доедут до новых.");
            text.AppendLine("        /// </summary>");
            text.AppendLine("        void __Reraise(ref global::Unity.Entities.SystemState state)");
            text.AppendLine("        {");
            text.Append("            if (!global::Blobcheg.BlobchegFileVersions.Changed(").Append(typeName)
                .AppendLine(".FileName, ref __seen))");
            text.AppendLine("                return;");
            text.AppendLine();
            text.Append("            var reload = global::Blobcheg.BlobchegTransport.Default.Read(").Append(typeName)
                .AppendLine(".FileName, global::Unity.Collections.Allocator.Persistent);");
            text.AppendLine("            try");
            text.AppendLine("            {");
            text.AppendLine("                // В редакторе ждать файл можно: это локальный диск, а не StreamingAssets в APK.");
            text.AppendLine("                reload.Complete();");
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
            text.AppendLine("            catch");
            text.AppendLine("            {");
            text.AppendLine("                // Владение уже ушло из чтения: не освободить буфер здесь — значит утечь базой,");
            text.AppendLine("                // а прежняя в синглтоне пока цела, и мир едет на ней дальше.");
            text.AppendLine("                buffer.Dispose();");
            text.AppendLine("                throw;");
            text.AppendLine("            }");
            text.AppendLine();
            text.AppendLine("            // Джобы, читающие прежний буфер, обязаны закончить до того, как он освободится.");
            text.AppendLine("            state.EntityManager.CompleteAllTrackedJobs();");
            text.AppendLine();
            text.Append("            var stale = __query.GetSingleton<").Append(typeName).AppendLine(">();");
            text.AppendLine("            stale.Dispose();");
            text.AppendLine();
            text.AppendLine("            __query.SetSingleton(fresh);");

            if (sweep)
                text.AppendLine("            global::Blobcheg.BlobchegSweep.Run(state.EntityManager);");

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

        // ---------------------------------------------------------------- мелочь

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

        /// <summary>Имя как его видит рефлексия: вложенные через '+'. Едитор считает хеш по нему же.</summary>
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
        /// fnv1a-64 по доменам и именам членов в порядке бит плюс ширина маски. Продублирован в
        /// <c>BlobchegRouterFormat.LayoutHash</c> — менять только парно, на этом стоит вся сходимость
        /// нумерации бит между кодогеном и едитором.
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
