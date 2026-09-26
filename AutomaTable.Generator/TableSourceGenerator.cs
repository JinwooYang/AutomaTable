using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AutomaTable.Generator
{
    [Generator]
    public sealed class TableSourceGenerator : IIncrementalGenerator
    {
        private const string TableRowAttributeName = "AutomaTable.Annotations.TableRowAttribute";
        private const string FindByAttributeName = "AutomaTable.Annotations.FindByAttribute";
        private const string FindAllByAttributeName = "AutomaTable.Annotations.FindAllByAttribute";

        private static readonly DiagnosticDescriptor UnsupportedMemberType = new(
            "TABLE001",
            "Unsupported table member type",
            "Member '{0}' on table row '{1}' uses unsupported type '{2}'",
            "AutomaTable.Generator",
            DiagnosticSeverity.Error,
            true);

        private static readonly DiagnosticDescriptor MissingFinderMember = new(
            "TABLE002",
            "Finder member does not exist",
            "Finder on table row '{0}' references missing or unsupported member '{1}'",
            "AutomaTable.Generator",
            DiagnosticSeverity.Error,
            true);

        private static readonly DiagnosticDescriptor DuplicateGeneratedName = new(
            "TABLE003",
            "Generated table name is duplicated",
            "Table rows '{0}' and '{1}' both generate the name '{2}'",
            "AutomaTable.Generator",
            DiagnosticSeverity.Error,
            true);

        private static readonly DiagnosticDescriptor MutableTableMember = new(
            "TABLE004",
            "Table row members must be externally immutable",
            "Member '{0}' on table row '{1}' must be a public property with a public getter and an internal setter",
            "AutomaTable.Generator",
            DiagnosticSeverity.Error,
            true);

        private static readonly DiagnosticDescriptor BlobFinderMember = new(
            "TABLE005",
            "BLOB members cannot be finder keys",
            "Finder on table row '{0}' cannot use BLOB member '{1}' as a key",
            "AutomaTable.Generator",
            DiagnosticSeverity.Error,
            true);

        private static readonly DiagnosticDescriptor InvalidIdentityMember = new(
            "TABLE006",
            "Table rows require a self-typed Id",
            "Table row '{0}' must declare 'public Id<{1}> Id {{ get; internal set; }}'",
            "AutomaTable.Generator",
            DiagnosticSeverity.Error,
            true);

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var analyses = context.SyntaxProvider.ForAttributeWithMetadataName(
                    TableRowAttributeName,
                    static (node, _) => node is ClassDeclarationSyntax,
                    static (attributeContext, _) => (INamedTypeSymbol)attributeContext.TargetSymbol)
                .Select(static (symbol, _) => AnalyzeModel(symbol));

            context.RegisterSourceOutput(analyses, static (sourceContext, analysis) =>
            {
                foreach (var diagnostic in analysis.Diagnostics)
                {
                    sourceContext.ReportDiagnostic(diagnostic);
                }
            });

            var models = analyses
                .Where(static analysis => analysis.Model != null)
                .Select(static (analysis, _) => analysis.Model!)
                .WithComparer(TableModelComparer.Instance);

            context.RegisterSourceOutput(models, static (sourceContext, model) =>
            {
                sourceContext.AddSource(
                    GetTableHintName(model),
                    SourceText.From(RenderTableFile(model), Encoding.UTF8));
            });

            var identities = analyses
                .Where(static analysis => analysis.Model != null)
                .Select(static (analysis, _) => analysis.Model!.Identity)
                .Collect();

            context.RegisterSourceOutput(identities, static (sourceContext, values) =>
                EmitDatabase(sourceContext, values));
        }

        private static void EmitDatabase(SourceProductionContext context, ImmutableArray<TableIdentity> identities)
        {
            if (identities.IsDefaultOrEmpty)
            {
                return;
            }

            var ordered = identities
                .OrderBy(static identity => identity.ModelType, StringComparer.Ordinal)
                .ToArray();
            var unique = new List<TableIdentity>(ordered.Length);
            var generatedNames = new Dictionary<string, TableIdentity>(StringComparer.Ordinal);

            foreach (var identity in ordered)
            {
                if (generatedNames.TryGetValue(identity.TableName, out var existing))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DuplicateGeneratedName,
                        identity.Location,
                        existing.ModelType,
                        identity.ModelType,
                        identity.TableName));
                    continue;
                }

                generatedNames.Add(identity.TableName, identity);
                unique.Add(identity);
            }

            if (unique.Count > 0)
            {
                context.AddSource(
                    "AutomaTable.Runtime.TableDatabase.g.cs",
                    SourceText.From(RenderDatabaseFile(unique), Encoding.UTF8));
            }
        }

        private static TableAnalysis AnalyzeModel(INamedTypeSymbol symbol)
        {
            var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
            var declaredIdMember = symbol.GetMembers("Id").FirstOrDefault(static member => !member.IsStatic);
            if (declaredIdMember is not IPropertySymbol idProperty ||
                !IsRequiredIdentityProperty(idProperty, symbol))
            {
                diagnostics.Add(Diagnostic.Create(
                    InvalidIdentityMember,
                    declaredIdMember?.Locations.FirstOrDefault() ?? symbol.Locations.FirstOrDefault(),
                    symbol.ToDisplayString(),
                    symbol.Name));
                return new TableAnalysis(null, diagnostics.ToImmutable());
            }

            var members = new List<MemberModel>();
            foreach (var symbolMember in symbol.GetMembers()
                         .OrderBy(static member => member.Locations.FirstOrDefault()?.SourceSpan.Start ?? int.MaxValue))
            {
                ITypeSymbol? memberType = null;
                if (symbolMember is IFieldSymbol field && !field.IsStatic &&
                    field.DeclaredAccessibility == Accessibility.Public)
                {
                    diagnostics.Add(Diagnostic.Create(
                        MutableTableMember,
                        field.Locations.FirstOrDefault(),
                        field.Name,
                        symbol.ToDisplayString()));
                    return new TableAnalysis(null, diagnostics.ToImmutable());
                }
                else if (symbolMember is IPropertySymbol property &&
                         !property.IsStatic &&
                         property.DeclaredAccessibility == Accessibility.Public &&
                         property.GetMethod?.DeclaredAccessibility == Accessibility.Public &&
                         property.SetMethod?.DeclaredAccessibility == Accessibility.Internal)
                {
                    memberType = property.Type;
                }
                else if (symbolMember is IPropertySymbol invalidProperty &&
                         !invalidProperty.IsStatic &&
                         invalidProperty.DeclaredAccessibility == Accessibility.Public)
                {
                    diagnostics.Add(Diagnostic.Create(
                        MutableTableMember,
                        invalidProperty.Locations.FirstOrDefault(),
                        invalidProperty.Name,
                        symbol.ToDisplayString()));
                    return new TableAnalysis(null, diagnostics.ToImmutable());
                }

                if (memberType == null)
                {
                    continue;
                }

                var conversion = GetConversion(memberType);
                if (conversion == null)
                {
                    diagnostics.Add(Diagnostic.Create(
                        UnsupportedMemberType,
                        symbolMember.Locations.FirstOrDefault(),
                        symbolMember.Name,
                        symbol.ToDisplayString(),
                        memberType.ToDisplayString()));
                    return new TableAnalysis(null, diagnostics.ToImmutable());
                }

                members.Add(new MemberModel(
                    symbolMember.Name,
                    memberType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    conversion));
            }

            var identityMember = members.First(member => member.Name == "Id");
            var finders = new List<FinderModel>
            {
                new FinderModel(false, new[] { identityMember }, false)
            };
            foreach (var attribute in symbol.GetAttributes())
            {
                var attributeName = attribute.AttributeClass?.ToDisplayString();
                bool findAll;
                if (attributeName == FindByAttributeName)
                {
                    findAll = false;
                }
                else if (attributeName == FindAllByAttributeName)
                {
                    findAll = true;
                }
                else
                {
                    continue;
                }

                var arguments = GetFinderArguments(attribute);
                if (arguments.Length == 0)
                {
                    continue;
                }

                var finderMembers = new List<MemberModel>();
                for (var i = 0; i < arguments.Length; i++)
                {
                    var keyName = arguments[i].Value as string;
                    var member = members.FirstOrDefault(candidate => candidate.Name == keyName);
                    if (member == null)
                    {
                        diagnostics.Add(Diagnostic.Create(
                            MissingFinderMember,
                            attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? symbol.Locations.FirstOrDefault(),
                            symbol.ToDisplayString(),
                            keyName ?? string.Empty));
                        return new TableAnalysis(null, diagnostics.ToImmutable());
                    }

                    if (member.Conversion.StorageKind == SqliteStorageKind.RequiredBlob ||
                        member.Conversion.StorageKind == SqliteStorageKind.NullableBlob)
                    {
                        diagnostics.Add(Diagnostic.Create(
                            BlobFinderMember,
                            attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? symbol.Locations.FirstOrDefault(),
                            symbol.ToDisplayString(),
                            member.Name));
                        return new TableAnalysis(null, diagnostics.ToImmutable());
                    }

                    finderMembers.Add(member);
                }

                if (!finders.Any(existing =>
                        existing.FindAll == findAll && existing.Members.SequenceEqual(finderMembers)))
                {
                    finders.Add(new FinderModel(findAll, finderMembers, true));
                }
            }

            var rowName = symbol.Name;
            var tableName = rowName.EndsWith("Data", StringComparison.Ordinal) && rowName.Length > 4
                ? rowName.Substring(0, rowName.Length - 4)
                : rowName;

            var modelType = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var model = new TableModel(
                modelType,
                rowName,
                tableName,
                members,
                finders,
                new TableIdentity(modelType, rowName, tableName, symbol.Locations.FirstOrDefault()));
            return new TableAnalysis(model, diagnostics.ToImmutable());
        }

        private static bool IsRequiredIdentityProperty(IPropertySymbol property, INamedTypeSymbol rowType)
        {
            if (property.IsStatic ||
                property.DeclaredAccessibility != Accessibility.Public ||
                property.GetMethod?.DeclaredAccessibility != Accessibility.Public ||
                property.SetMethod?.DeclaredAccessibility != Accessibility.Internal)
            {
                return false;
            }

            return property.Type is INamedTypeSymbol idType &&
                   idType.IsGenericType &&
                   idType.Name == "Id" &&
                   idType.Arity == 1 &&
                   idType.ContainingNamespace.ToDisplayString() == "AutomaTable.Primitives" &&
                   SymbolEqualityComparer.Default.Equals(idType.TypeArguments[0], rowType);
        }

        private static ImmutableArray<TypedConstant> GetFinderArguments(AttributeData attribute)
        {
            if (attribute.ConstructorArguments.Length == 1 &&
                attribute.ConstructorArguments[0].Kind == TypedConstantKind.Array)
            {
                return attribute.ConstructorArguments[0].Values;
            }

            return attribute.ConstructorArguments;
        }

        private static TypeConversion? GetConversion(ITypeSymbol type)
        {
            if (type is INamedTypeSymbol named && named.IsGenericType &&
                named.Name == "Id" && named.Arity == 1 &&
                named.ContainingNamespace.ToDisplayString() == "AutomaTable.Primitives")
            {
                var modelType = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                return new TypeConversion(SqliteStorageKind.Integer, "{0}.RawId", "new " + modelType + "({0})");
            }

            if (type.TypeKind == TypeKind.Enum)
            {
                var modelType = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                return new TypeConversion(SqliteStorageKind.Integer, "(long){0}", "(" + modelType + "){0}");
            }

            if (type.Name == "AssetAddress" && type.ContainingNamespace.ToDisplayString() == "AutomaTable.Primitives")
            {
                return new TypeConversion(SqliteStorageKind.RequiredText, "{0}.Value", "new global::AutomaTable.Primitives.AssetAddress({0})");
            }

            switch (type.SpecialType)
            {
                case SpecialType.System_Boolean:
                    return new TypeConversion(SqliteStorageKind.Integer, "({0} ? 1L : 0L)", "({0} != 0L)");
                case SpecialType.System_Byte:
                    return new TypeConversion(SqliteStorageKind.Integer, "(long){0}", "(byte){0}");
                case SpecialType.System_SByte:
                    return new TypeConversion(SqliteStorageKind.Integer, "(long){0}", "(sbyte){0}");
                case SpecialType.System_Int16:
                    return new TypeConversion(SqliteStorageKind.Integer, "(long){0}", "(short){0}");
                case SpecialType.System_UInt16:
                    return new TypeConversion(SqliteStorageKind.Integer, "(long){0}", "(ushort){0}");
                case SpecialType.System_Int32:
                    return new TypeConversion(SqliteStorageKind.Integer, "(long){0}", "(int){0}");
                case SpecialType.System_UInt32:
                    return new TypeConversion(SqliteStorageKind.Integer, "(long){0}", "(uint){0}");
                case SpecialType.System_Int64:
                    return new TypeConversion(SqliteStorageKind.Integer, "{0}", "{0}");
                case SpecialType.System_UInt64:
                    return new TypeConversion(SqliteStorageKind.Integer, "checked((long){0})", "(ulong){0}");
                case SpecialType.System_Single:
                    return new TypeConversion(SqliteStorageKind.Real, "(double){0}", "(float){0}");
                case SpecialType.System_Double:
                    return new TypeConversion(SqliteStorageKind.Real, "{0}", "{0}");
                case SpecialType.System_Decimal:
                    return new TypeConversion(SqliteStorageKind.Real, "(double){0}", "(decimal){0}");
                case SpecialType.System_String:
                    return new TypeConversion(
                        type.NullableAnnotation == NullableAnnotation.Annotated
                            ? SqliteStorageKind.NullableText
                            : SqliteStorageKind.RequiredText,
                        "{0}",
                        "{0}");
            }

            if (IsReadOnlyMemoryOfByte(type))
            {
                return new TypeConversion(SqliteStorageKind.RequiredBlob, "{0}", "{0}");
            }

            if (type is INamedTypeSymbol nullable &&
                nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
                nullable.TypeArguments.Length == 1)
            {
                var underlyingType = nullable.TypeArguments[0];
                if (IsReadOnlyMemoryOfByte(underlyingType))
                {
                    return new TypeConversion(SqliteStorageKind.NullableBlob, "{0}", "{0}");
                }

                if (underlyingType is INamedTypeSymbol nullableId && nullableId.IsGenericType &&
                    nullableId.Name == "Id" && nullableId.Arity == 1 &&
                    nullableId.ContainingNamespace.ToDisplayString() == "AutomaTable.Primitives")
                {
                    var idType = underlyingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    return new TypeConversion(
                        SqliteStorageKind.NullableInteger,
                        "{0}.HasValue ? {0}.Value.RawId : (long?)null",
                        "{0}.HasValue ? new " + idType + "({0}.GetValueOrDefault()) : (" + idType + "?)null");
                }

                if (underlyingType.Name == "AssetAddress" &&
                    underlyingType.ContainingNamespace.ToDisplayString() == "AutomaTable.Primitives")
                {
                    return new TypeConversion(
                        SqliteStorageKind.NullableText,
                        "{0}.HasValue ? {0}.Value.Value : null",
                        "{0} == null ? (global::AutomaTable.Primitives.AssetAddress?)null : new global::AutomaTable.Primitives.AssetAddress({0}!)");
                }
            }

            var fullyQualified = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (fullyQualified == "global::System.DateTime")
                return new TypeConversion(SqliteStorageKind.Integer, "{0}.Ticks", "new global::System.DateTime({0})");
            if (fullyQualified == "global::System.DateTimeOffset")
                return new TypeConversion(SqliteStorageKind.Integer, "{0}.UtcTicks", "new global::System.DateTimeOffset({0}, global::System.TimeSpan.Zero)");
            if (fullyQualified == "global::System.TimeSpan")
                return new TypeConversion(SqliteStorageKind.Integer, "{0}.Ticks", "new global::System.TimeSpan({0})");
            if (fullyQualified == "global::System.Guid")
                return new TypeConversion(SqliteStorageKind.RequiredText, "{0}.ToString()", "new global::System.Guid({0})");

            return null;
        }

        private static bool IsReadOnlyMemoryOfByte(ITypeSymbol type)
        {
            return type is INamedTypeSymbol named &&
                   named.Name == "ReadOnlyMemory" &&
                   named.Arity == 1 &&
                   named.ContainingNamespace.ToDisplayString() == "System" &&
                   named.TypeArguments[0].SpecialType == SpecialType.System_Byte;
        }

        private static string RenderDatabaseFile(IReadOnlyList<TableIdentity> identities)
        {
            var builder = new StringBuilder();
            builder.AppendLine("// <auto-generated />");
            builder.AppendLine("#nullable enable");
            builder.AppendLine();
            builder.AppendLine("namespace AutomaTable.Runtime");
            builder.AppendLine("{");
            RenderDatabase(builder, identities);
            builder.AppendLine("}");
            return builder.ToString();
        }

        private static string RenderTableFile(TableModel model)
        {
            var builder = new StringBuilder();
            builder.AppendLine("// <auto-generated />");
            builder.AppendLine("#nullable enable");
            builder.AppendLine();
            builder.AppendLine("namespace AutomaTable.Runtime");
            builder.AppendLine("{");
            RenderTable(builder, model);
            builder.AppendLine("}");
            return builder.ToString();
        }

        private static void RenderDatabase(StringBuilder builder, IReadOnlyList<TableIdentity> identities)
        {
            builder.AppendLine("    public sealed class TableDatabase : global::System.IDisposable");
            builder.AppendLine("    {");
            builder.AppendLine("        private global::SQLite.SQLiteConnection? _connection;");
            builder.AppendLine("        private int _initializeState;");
            builder.AppendLine("        private bool _disposed;");
            builder.AppendLine();
            foreach (var identity in identities)
            {
                builder.Append("        public ").Append(identity.TableName).Append("Table ")
                    .Append(identity.TableName).AppendLine(" { get; private set; } = null!;");
            }
            builder.AppendLine();
            builder.AppendLine("        public global::System.Threading.Tasks.Task InitializeAsync(string dbPath)");
            builder.AppendLine("        {");
            builder.AppendLine("            return InitializeAsync(dbPath, global::AutomaTable.Runtime.TableDatabaseOptions.Direct);");
            builder.AppendLine("        }");
            builder.AppendLine();
            builder.AppendLine("        public async global::System.Threading.Tasks.Task InitializeAsync(string dbPath, global::AutomaTable.Runtime.TableDatabaseOptions options)");
            builder.AppendLine("        {");
            builder.AppendLine("            if (dbPath == null) throw new global::System.ArgumentNullException(nameof(dbPath));");
            builder.AppendLine("            if (!global::System.IO.File.Exists(dbPath)) throw new global::System.IO.FileNotFoundException(\"The table database file does not exist.\", dbPath);");
            builder.AppendLine("            if (options.LoadMode != global::AutomaTable.Runtime.TableLoadMode.Direct && options.LoadMode != global::AutomaTable.Runtime.TableLoadMode.Preload)");
            builder.AppendLine("                throw new global::System.ArgumentOutOfRangeException(nameof(options));");
            builder.AppendLine("            if (_disposed) throw new global::System.ObjectDisposedException(nameof(TableDatabase));");
            builder.AppendLine("            if (global::System.Threading.Interlocked.CompareExchange(ref _initializeState, 1, 0) != 0)");
            builder.AppendLine("                throw new global::System.InvalidOperationException(\"The database is already initialized or initialization is in progress.\");");
            builder.AppendLine();
            builder.AppendLine("            global::SQLite.SQLiteConnection connection;");
            builder.AppendLine("            try");
            builder.AppendLine("            {");
            builder.AppendLine("                connection = await global::System.Threading.Tasks.Task.Run(() =>");
            builder.AppendLine("            {");
            builder.AppendLine("                var opened = new global::SQLite.SQLiteConnection(dbPath, global::SQLite.SQLiteOpenFlags.ReadOnly | global::SQLite.SQLiteOpenFlags.FullMutex);");
            builder.AppendLine("                try");
            builder.AppendLine("                {");
            foreach (var identity in identities)
            {
                builder.Append("                    ").Append(identity.TableName)
                    .AppendLine("Table.ValidateSchema(opened.Handle);");
            }
            builder.AppendLine("                    return opened;");
            builder.AppendLine("                }");
            builder.AppendLine("                catch");
            builder.AppendLine("                {");
            builder.AppendLine("                    opened.Dispose();");
            builder.AppendLine("                    throw;");
            builder.AppendLine("                }");
            builder.AppendLine("                }).ConfigureAwait(false);");
            builder.AppendLine("            }");
            builder.AppendLine("            catch");
            builder.AppendLine("            {");
            builder.AppendLine("                global::System.Threading.Volatile.Write(ref _initializeState, 0);");
            builder.AppendLine("                throw;");
            builder.AppendLine("            }");
            builder.AppendLine();
            builder.AppendLine("            if (_disposed)");
            builder.AppendLine("            {");
            builder.AppendLine("                connection.Dispose();");
            builder.AppendLine("                throw new global::System.ObjectDisposedException(nameof(TableDatabase));");
            builder.AppendLine("            }");
            builder.AppendLine();
            builder.AppendLine("            if (options.LoadMode == global::AutomaTable.Runtime.TableLoadMode.Preload)");
            builder.AppendLine("            {");
            builder.AppendLine("                try");
            builder.AppendLine("                {");
            builder.AppendLine("                    await global::System.Threading.Tasks.Task.Run(() =>");
            builder.AppendLine("                    {");
            foreach (var identity in identities)
            {
                builder.Append("                        ").Append(identity.TableName).Append(" = ")
                    .Append(identity.TableName).AppendLine("Table.CreatePreloaded(connection);");
            }
            builder.AppendLine("                    }).ConfigureAwait(false);");
            builder.AppendLine("                }");
            builder.AppendLine("                catch");
            builder.AppendLine("                {");
            builder.AppendLine("                    global::System.Threading.Volatile.Write(ref _initializeState, 0);");
            builder.AppendLine("                    throw;");
            builder.AppendLine("                }");
            builder.AppendLine("                finally");
            builder.AppendLine("                {");
            builder.AppendLine("                    connection.Dispose();");
            builder.AppendLine("                }");
            builder.AppendLine("                return;");
            builder.AppendLine("            }");
            builder.AppendLine();
            builder.AppendLine("            _connection = connection;");
            foreach (var identity in identities)
            {
                builder.Append("            ").Append(identity.TableName).Append(" = new ")
                    .Append(identity.TableName).AppendLine("Table(connection);");
            }
            builder.AppendLine("        }");
            builder.AppendLine();
            builder.AppendLine("        public void Dispose()");
            builder.AppendLine("        {");
            builder.AppendLine("            if (_disposed) return;");
            builder.AppendLine("            _disposed = true;");
            builder.AppendLine("            _connection?.Dispose();");
            builder.AppendLine("            _connection = null;");
            builder.AppendLine("        }");
            builder.AppendLine("    }");
            builder.AppendLine();
        }

        private static void RenderTable(StringBuilder builder, TableModel model)
        {
            builder.Append("    public sealed class ").Append(model.TableName).AppendLine("Table");
            builder.AppendLine("    {");
            builder.AppendLine("        private readonly global::SQLite.SQLiteConnection? _connection;");
            foreach (var finder in model.Finders)
            {
                builder.Append("        private readonly ");
                if (finder.FindAll)
                {
                    builder.Append("global::System.Collections.Generic.Dictionary<")
                        .Append(GetFinderKeyType(finder)).Append(", global::System.Collections.Generic.List<")
                        .Append(model.ModelType).Append(">>");
                }
                else
                {
                    builder.Append("global::System.Collections.Generic.Dictionary<")
                        .Append(GetFinderKeyType(finder)).Append(", ").Append(model.ModelType).Append('>');
                }

                builder.Append("? ").Append(GetPreloadedIndexFieldName(finder)).AppendLine(";");
            }
            builder.AppendLine();
            builder.Append("        internal ").Append(model.TableName)
                .AppendLine("Table(global::SQLite.SQLiteConnection connection)");
            builder.AppendLine("        {");
            builder.AppendLine("            _connection = connection;");
            builder.AppendLine("        }");
            builder.AppendLine();

            builder.Append("        private ").Append(model.TableName)
                .Append("Table(global::System.Collections.Generic.IReadOnlyList<")
                .Append(model.ModelType).AppendLine("> rows)");
            builder.AppendLine("        {");
            foreach (var finder in model.Finders)
            {
                builder.Append("            ").Append(GetPreloadedIndexFieldName(finder))
                    .Append(" = new ");
                if (finder.FindAll)
                {
                    builder.Append("global::System.Collections.Generic.Dictionary<")
                        .Append(GetFinderKeyType(finder)).Append(", global::System.Collections.Generic.List<")
                        .Append(model.ModelType).AppendLine(">>();");
                }
                else
                {
                    builder.Append("global::System.Collections.Generic.Dictionary<")
                        .Append(GetFinderKeyType(finder)).Append(", ").Append(model.ModelType)
                        .AppendLine(">();");
                }
            }
            builder.AppendLine("            foreach (var row in rows)");
            builder.AppendLine("            {");
            foreach (var finder in model.Finders)
            {
                var rowMembers = finder.Members
                    .Select(static member => "row." + EscapeIdentifier(member.Name))
                    .ToArray();
                var indexField = GetPreloadedIndexFieldName(finder);
                if (finder.FindAll)
                {
                    builder.AppendLine("                {");
                    builder.Append("                    var key = ").Append(GetKeyExpression(rowMembers)).AppendLine(";");
                    builder.Append("                    if (!").Append(indexField).AppendLine(".TryGetValue(key, out var values))");
                    builder.AppendLine("                    {");
                    builder.Append("                        values = new global::System.Collections.Generic.List<")
                        .Append(model.ModelType).AppendLine(">();");
                    builder.Append("                        ").Append(indexField).AppendLine(".Add(key, values);");
                    builder.AppendLine("                    }");
                    builder.AppendLine("                    values.Add(row);");
                    builder.AppendLine("                }");
                }
                else
                {
                    builder.Append("                ").Append(indexField)
                        .Append(".Add(").Append(GetKeyExpression(rowMembers)).AppendLine(", row);");
                }
            }
            builder.AppendLine("            }");
            builder.AppendLine("        }");
            builder.AppendLine();

            builder.Append("        internal static ").Append(model.TableName)
                .AppendLine("Table CreatePreloaded(global::SQLite.SQLiteConnection connection)");
            builder.AppendLine("        {");
            builder.Append("            return new ").Append(model.TableName)
                .AppendLine("Table(ReadAll(connection.Handle));");
            builder.AppendLine("        }");
            builder.AppendLine();

            builder.AppendLine("        internal static void ValidateSchema(global::SQLitePCL.sqlite3 database)");
            builder.AppendLine("        {");
            builder.Append("            global::AutomaTable.Runtime.Internal.SqliteRuntime.ValidateTable(database, \"")
                .Append(EscapeString(model.RowName)).Append("\", new[] { ");
            for (var i = 0; i < model.Members.Count; i++)
            {
                if (i > 0) builder.Append(", ");
                builder.Append('"').Append(EscapeString(model.Members[i].Name)).Append('"');
            }
            builder.AppendLine(" }, \"Id\");");
            foreach (var finder in model.Finders
                         .Where(static value => value.RequiresSqliteIndex)
                         .GroupBy(static value => string.Join("_", value.Members.Select(static member => member.Name)), StringComparer.Ordinal)
                         .Select(static group => group.First()))
            {
                builder.Append("            global::AutomaTable.Runtime.Internal.SqliteRuntime.ValidateIndex(database, \"")
                    .Append(EscapeString(model.RowName)).Append("\", \"IX_")
                    .Append(EscapeString(model.RowName)).Append('_')
                    .Append(EscapeString(string.Join("_", finder.Members.Select(static member => member.Name))))
                    .Append("\", new[] { ");
                for (var i = 0; i < finder.Members.Count; i++)
                {
                    if (i > 0) builder.Append(", ");
                    builder.Append('"').Append(EscapeString(finder.Members[i].Name)).Append('"');
                }
                builder.Append(" }, ").Append(finder.FindAll ? "false" : "true").AppendLine(");");
            }
            builder.AppendLine("        }");
            builder.AppendLine();

            foreach (var finder in model.Finders)
            {
                RenderFinder(builder, model, finder);
            }

            builder.Append("        private static global::System.Collections.Generic.IReadOnlyList<")
                .Append(model.ModelType).AppendLine("> ReadAll(global::SQLitePCL.sqlite3 database)");
            builder.AppendLine("        {");
            builder.Append("            var rows = new global::System.Collections.Generic.List<")
                .Append(model.ModelType).AppendLine(">();");
            builder.Append("            var statement = global::AutomaTable.Runtime.Internal.SqliteRuntime.Prepare(database, \"SELECT ");
            for (var i = 0; i < model.Members.Count; i++)
            {
                if (i > 0) builder.Append(", ");
                builder.Append("\\\"").Append(EscapeString(model.Members[i].Name)).Append("\\\"");
            }
            builder.Append(" FROM \\\"").Append(EscapeString(model.RowName)).AppendLine("\\\"\");");
            builder.AppendLine("            try");
            builder.AppendLine("            {");
            builder.AppendLine("                while (true)");
            builder.AppendLine("                {");
            builder.AppendLine("                    var stepResult = global::SQLitePCL.raw.sqlite3_step(statement);");
            builder.AppendLine("                    if (stepResult == global::SQLitePCL.raw.SQLITE_ROW)");
            builder.AppendLine("                    {");
            builder.AppendLine("                        rows.Add(ReadModel(statement));");
            builder.AppendLine("                        continue;");
            builder.AppendLine("                    }");
            builder.AppendLine("                    if (stepResult == global::SQLitePCL.raw.SQLITE_DONE) break;");
            builder.AppendLine("                    throw global::AutomaTable.Runtime.Internal.SqliteRuntime.CreateException(database, \"preload table\", stepResult);");
            builder.AppendLine("                }");
            builder.AppendLine("            }");
            builder.AppendLine("            finally");
            builder.AppendLine("            {");
            builder.AppendLine("                global::SQLitePCL.raw.sqlite3_finalize(statement);");
            builder.AppendLine("            }");
            builder.AppendLine("            return rows;");
            builder.AppendLine("        }");
            builder.AppendLine();

            builder.Append("        private static ").Append(model.ModelType)
                .AppendLine(" ReadModel(global::SQLitePCL.sqlite3_stmt statement)");
            builder.AppendLine("        {");
            builder.Append("            return new ").Append(model.ModelType).AppendLine();
            builder.AppendLine("            {");
            for (var i = 0; i < model.Members.Count; i++)
            {
                var member = model.Members[i];
                builder.Append("                ").Append(EscapeIdentifier(member.Name)).Append(" = ")
                    .Append(string.Format(
                        member.Conversion.FromStorageFormat,
                        GetReadExpression(member.Conversion.StorageKind, "statement", i)));
                builder.AppendLine(i == model.Members.Count - 1 ? string.Empty : ",");
            }
            builder.AppendLine("            };");
            builder.AppendLine("        }");
            builder.AppendLine("    }");
            builder.AppendLine();
        }

        private static void RenderFinder(StringBuilder builder, TableModel model, FinderModel finder)
        {
            var methodName = GetMethodName(finder);
            var parameterNames = finder.Members.Select(static member => ToParameterName(member.Name)).ToArray();
            var returnType = finder.FindAll
                ? "global::System.Collections.Generic.IReadOnlyList<" + model.ModelType + ">"
                : model.ModelType + "?";

            builder.Append("        public ").Append(returnType).Append(' ').Append(methodName).Append('(');
            for (var i = 0; i < finder.Members.Count; i++)
            {
                if (i > 0) builder.Append(", ");
                builder.Append(finder.Members[i].ModelType).Append(' ').Append(parameterNames[i]);
            }
            builder.AppendLine(")");
            builder.AppendLine("        {");

            var preloadKey = GetKeyExpression(parameterNames);
            if (finder.FindAll)
            {
                builder.Append("            if (").Append(GetPreloadedIndexFieldName(finder))
                    .AppendLine(" != null)");
                builder.Append("                return ").Append(GetPreloadedIndexFieldName(finder))
                    .Append(".TryGetValue(").Append(preloadKey)
                    .Append(", out var preloaded) ? preloaded : global::System.Array.Empty<")
                    .Append(model.ModelType).AppendLine(">();");
            }
            else
            {
                builder.Append("            if (").Append(GetPreloadedIndexFieldName(finder))
                    .AppendLine(" != null)");
                builder.Append("                return ").Append(GetPreloadedIndexFieldName(finder))
                    .Append(".TryGetValue(").Append(preloadKey)
                    .AppendLine(", out var preloaded) ? preloaded : null;");
            }
            builder.AppendLine();
            builder.AppendLine("            var connection = _connection ?? throw new global::System.InvalidOperationException(\"The table is not initialized.\");");

            if (finder.FindAll)
            {
                builder.Append("            var result = new global::System.Collections.Generic.List<")
                    .Append(model.ModelType).AppendLine(">();");
            }
            else
            {
                builder.Append("            ").Append(model.ModelType).AppendLine("? result;");
            }

            builder.Append("            var statement = global::AutomaTable.Runtime.Internal.SqliteRuntime.Prepare(connection.Handle, \"SELECT ");
            for (var i = 0; i < model.Members.Count; i++)
            {
                if (i > 0) builder.Append(", ");
                builder.Append("\\\"").Append(EscapeString(model.Members[i].Name)).Append("\\\"");
            }
            builder.Append(" FROM \\\"").Append(EscapeString(model.RowName)).Append("\\\" WHERE ");
            for (var i = 0; i < finder.Members.Count; i++)
            {
                if (i > 0) builder.Append(" AND ");
                builder.Append("\\\"").Append(EscapeString(finder.Members[i].Name)).Append("\\\" = ?");
            }
            if (!finder.FindAll)
            {
                builder.Append(" LIMIT 1");
            }
            builder.AppendLine("\");");
            builder.AppendLine("            try");
            builder.AppendLine("            {");
            for (var i = 0; i < finder.Members.Count; i++)
            {
                builder.Append("                global::AutomaTable.Runtime.Internal.SqliteRuntime.")
                    .Append(GetBindMethod(finder.Members[i].Conversion.StorageKind))
                    .Append("(connection.Handle, statement, ").Append(i + 1).Append(", ")
                    .Append(string.Format(finder.Members[i].Conversion.ToStorageFormat, parameterNames[i]))
                    .AppendLine(");");
            }

            if (finder.FindAll)
            {
                builder.AppendLine("                while (true)");
                builder.AppendLine("                {");
                builder.AppendLine("                    var stepResult = global::SQLitePCL.raw.sqlite3_step(statement);");
                builder.AppendLine("                    if (stepResult == global::SQLitePCL.raw.SQLITE_ROW)");
                builder.AppendLine("                    {");
                builder.AppendLine("                        result.Add(ReadModel(statement));");
                builder.AppendLine("                        continue;");
                builder.AppendLine("                    }");
                builder.AppendLine("                    if (stepResult == global::SQLitePCL.raw.SQLITE_DONE) break;");
                builder.AppendLine("                    throw global::AutomaTable.Runtime.Internal.SqliteRuntime.CreateException(connection.Handle, \"step query\", stepResult);");
                builder.AppendLine("                }");
            }
            else
            {
                builder.AppendLine("                var stepResult = global::SQLitePCL.raw.sqlite3_step(statement);");
                builder.AppendLine("                if (stepResult == global::SQLitePCL.raw.SQLITE_ROW)");
                builder.AppendLine("                    result = ReadModel(statement);");
                builder.AppendLine("                else if (stepResult == global::SQLitePCL.raw.SQLITE_DONE)");
                builder.AppendLine("                    result = null;");
                builder.AppendLine("                else");
                builder.AppendLine("                    throw global::AutomaTable.Runtime.Internal.SqliteRuntime.CreateException(connection.Handle, \"step query\", stepResult);");
            }
            builder.AppendLine("            }");
            builder.AppendLine("            finally");
            builder.AppendLine("            {");
            builder.AppendLine("                global::SQLitePCL.raw.sqlite3_finalize(statement);");
            builder.AppendLine("            }");

            builder.AppendLine("            return result;");
            builder.AppendLine("        }");
            builder.AppendLine();
        }

        private static string GetTableHintName(TableModel model)
        {
            return "AutomaTable.Runtime." + model.TableName + "Table.g.cs";
        }

        private static string GetMethodName(FinderModel finder)
        {
            return (finder.FindAll ? "FindAllBy" : "FindBy") +
                   string.Join("And", finder.Members.Select(static member => member.Name));
        }

        private static string GetPreloadedIndexFieldName(FinderModel finder)
        {
            var methodName = GetMethodName(finder);
            return "_" + char.ToLowerInvariant(methodName[0]) + methodName.Substring(1) + "PreloadedIndex";
        }

        private static string GetFinderKeyType(FinderModel finder)
        {
            return finder.Members.Count == 1
                ? finder.Members[0].ModelType
                : "(" + string.Join(", ", finder.Members.Select(static member => member.ModelType)) + ")";
        }

        private static string GetKeyExpression(IReadOnlyList<string> values)
        {
            return values.Count == 1
                ? values[0]
                : "(" + string.Join(", ", values) + ")";
        }

        private static string GetBindMethod(SqliteStorageKind storageKind)
        {
            switch (storageKind)
            {
                case SqliteStorageKind.Integer:
                    return "BindInt64";
                case SqliteStorageKind.NullableInteger:
                    return "BindNullableInt64";
                case SqliteStorageKind.Real:
                    return "BindDouble";
                case SqliteStorageKind.RequiredText:
                case SqliteStorageKind.NullableText:
                    return "BindText";
                case SqliteStorageKind.RequiredBlob:
                case SqliteStorageKind.NullableBlob:
                    return "BindBlob";
                default:
                    throw new ArgumentOutOfRangeException(nameof(storageKind));
            }
        }

        private static string GetReadExpression(SqliteStorageKind storageKind, string statementName, int columnIndex)
        {
            string methodName;
            switch (storageKind)
            {
                case SqliteStorageKind.Integer:
                    methodName = "ReadInt64";
                    break;
                case SqliteStorageKind.NullableInteger:
                    methodName = "ReadNullableInt64";
                    break;
                case SqliteStorageKind.Real:
                    methodName = "ReadDouble";
                    break;
                case SqliteStorageKind.RequiredText:
                    methodName = "ReadText";
                    break;
                case SqliteStorageKind.NullableText:
                    methodName = "ReadNullableText";
                    break;
                case SqliteStorageKind.RequiredBlob:
                    methodName = "ReadBlob";
                    break;
                case SqliteStorageKind.NullableBlob:
                    methodName = "ReadNullableBlob";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(storageKind));
            }

            return "global::AutomaTable.Runtime.Internal.SqliteRuntime." + methodName + "(" + statementName + ", " + columnIndex + ")";
        }

        private static string ToParameterName(string name)
        {
            return string.IsNullOrEmpty(name)
                ? "@value"
                : "@" + char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        private static string EscapeIdentifier(string name) => "@" + name;

        private static string EscapeString(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private sealed class TableAnalysis
        {
            public TableAnalysis(TableModel? model, ImmutableArray<Diagnostic> diagnostics)
            {
                Model = model;
                Diagnostics = diagnostics;
            }

            public TableModel? Model { get; }
            public ImmutableArray<Diagnostic> Diagnostics { get; }
        }

        private sealed class TableIdentity : IEquatable<TableIdentity>
        {
            private readonly string _sourcePath;
            private readonly int _sourceStart;

            public TableIdentity(string modelType, string rowName, string tableName, Location? location)
            {
                ModelType = modelType;
                RowName = rowName;
                TableName = tableName;
                Location = location;
                _sourcePath = location?.SourceTree?.FilePath ?? string.Empty;
                _sourceStart = location?.SourceSpan.Start ?? -1;
            }

            public string ModelType { get; }
            public string RowName { get; }
            public string TableName { get; }
            public Location? Location { get; }

            public bool Equals(TableIdentity? other)
            {
                return other != null &&
                       ModelType == other.ModelType &&
                       RowName == other.RowName &&
                       TableName == other.TableName &&
                       _sourcePath == other._sourcePath &&
                       _sourceStart == other._sourceStart;
            }

            public override bool Equals(object? obj) => Equals(obj as TableIdentity);

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = 17;
                    hash = hash * 31 + ModelType.GetHashCode();
                    hash = hash * 31 + RowName.GetHashCode();
                    hash = hash * 31 + TableName.GetHashCode();
                    hash = hash * 31 + _sourcePath.GetHashCode();
                    hash = hash * 31 + _sourceStart;
                    return hash;
                }
            }
        }

        private sealed class TableModel : IEquatable<TableModel>
        {
            public TableModel(
                string modelType,
                string rowName,
                string tableName,
                IReadOnlyList<MemberModel> members,
                IReadOnlyList<FinderModel> finders,
                TableIdentity identity)
            {
                ModelType = modelType;
                RowName = rowName;
                TableName = tableName;
                Members = members;
                Finders = finders;
                Identity = identity;
            }

            public string ModelType { get; }
            public string RowName { get; }
            public string TableName { get; }
            public IReadOnlyList<MemberModel> Members { get; }
            public IReadOnlyList<FinderModel> Finders { get; }
            public TableIdentity Identity { get; }

            public bool Equals(TableModel? other)
            {
                return other != null &&
                       ModelType == other.ModelType &&
                       RowName == other.RowName &&
                       TableName == other.TableName &&
                       Members.SequenceEqual(other.Members) &&
                       Finders.SequenceEqual(other.Finders);
            }

            public override bool Equals(object? obj) => Equals(obj as TableModel);

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = 17;
                    hash = hash * 31 + ModelType.GetHashCode();
                    hash = hash * 31 + RowName.GetHashCode();
                    hash = hash * 31 + TableName.GetHashCode();
                    foreach (var member in Members) hash = hash * 31 + member.GetHashCode();
                    foreach (var finder in Finders) hash = hash * 31 + finder.GetHashCode();
                    return hash;
                }
            }
        }

        private sealed class TableModelComparer : IEqualityComparer<TableModel>
        {
            public static readonly TableModelComparer Instance = new TableModelComparer();

            private TableModelComparer()
            {
            }

            public bool Equals(TableModel? x, TableModel? y)
            {
                return ReferenceEquals(x, y) || (x != null && x.Equals(y));
            }

            public int GetHashCode(TableModel obj) => obj.GetHashCode();
        }

        private sealed class MemberModel : IEquatable<MemberModel>
        {
            public MemberModel(string name, string modelType, TypeConversion conversion)
            {
                Name = name;
                ModelType = modelType;
                Conversion = conversion;
            }

            public string Name { get; }
            public string ModelType { get; }
            public TypeConversion Conversion { get; }

            public bool Equals(MemberModel? other)
            {
                return other != null &&
                       Name == other.Name &&
                       ModelType == other.ModelType &&
                       Conversion.Equals(other.Conversion);
            }

            public override bool Equals(object? obj) => Equals(obj as MemberModel);

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = 17;
                    hash = hash * 31 + Name.GetHashCode();
                    hash = hash * 31 + ModelType.GetHashCode();
                    hash = hash * 31 + Conversion.GetHashCode();
                    return hash;
                }
            }
        }

        private sealed class FinderModel : IEquatable<FinderModel>
        {
            public FinderModel(bool findAll, IReadOnlyList<MemberModel> members, bool requiresSqliteIndex)
            {
                FindAll = findAll;
                Members = members;
                RequiresSqliteIndex = requiresSqliteIndex;
            }

            public bool FindAll { get; }
            public IReadOnlyList<MemberModel> Members { get; }
            public bool RequiresSqliteIndex { get; }

            public bool Equals(FinderModel? other)
            {
                return other != null &&
                       FindAll == other.FindAll &&
                       RequiresSqliteIndex == other.RequiresSqliteIndex &&
                       Members.SequenceEqual(other.Members);
            }

            public override bool Equals(object? obj) => Equals(obj as FinderModel);

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = FindAll ? 1 : 0;
                    hash = hash * 31 + (RequiresSqliteIndex ? 1 : 0);
                    foreach (var member in Members) hash = hash * 31 + member.GetHashCode();
                    return hash;
                }
            }
        }

        private sealed class TypeConversion : IEquatable<TypeConversion>
        {
            public TypeConversion(SqliteStorageKind storageKind, string toStorageFormat, string fromStorageFormat)
            {
                StorageKind = storageKind;
                ToStorageFormat = toStorageFormat;
                FromStorageFormat = fromStorageFormat;
            }

            public SqliteStorageKind StorageKind { get; }
            public string ToStorageFormat { get; }
            public string FromStorageFormat { get; }

            public bool Equals(TypeConversion? other)
            {
                return other != null &&
                       StorageKind == other.StorageKind &&
                       ToStorageFormat == other.ToStorageFormat &&
                       FromStorageFormat == other.FromStorageFormat;
            }

            public override bool Equals(object? obj) => Equals(obj as TypeConversion);

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = (int)StorageKind;
                    hash = hash * 31 + ToStorageFormat.GetHashCode();
                    hash = hash * 31 + FromStorageFormat.GetHashCode();
                    return hash;
                }
            }
        }

        private enum SqliteStorageKind
        {
            Integer,
            NullableInteger,
            Real,
            RequiredText,
            NullableText,
            RequiredBlob,
            NullableBlob
        }
    }
}
