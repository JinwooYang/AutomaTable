using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace AutomaTable.Importer.Generator
{
    [Generator]
    public sealed class TableImporterSourceGenerator : IIncrementalGenerator
    {
        private const string TableRowAttributeName = "AutomaTable.Annotations.TableRowAttribute";
        private const string FindByAttributeName = "AutomaTable.Annotations.FindByAttribute";
        private const string FindAllByAttributeName = "AutomaTable.Annotations.FindAllByAttribute";
        private const string PrimitivesNamespace = "AutomaTable.Primitives";

        private static readonly DiagnosticDescriptor UnsupportedMemberType = new(
            "TABLEIMP001",
            "Unsupported importer member type",
            "Importer cannot map member '{0}' on table row '{1}' with type '{2}'",
            "AutomaTable.Importer.Generator",
            DiagnosticSeverity.Error,
            true);

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var analysis = context.CompilationProvider.Select(static (compilation, _) => Analyze(compilation));

            context.RegisterSourceOutput(analysis, static (sourceContext, result) =>
            {
                foreach (var diagnostic in result.Diagnostics)
                    sourceContext.ReportDiagnostic(diagnostic);

                if (result.Diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
                    return;

                foreach (var table in result.Tables)
                {
                    sourceContext.AddSource(
                        "AutomaTable.Importer.Generated." + table.RowName + "SheetImporter.g.cs",
                        SourceText.From(RenderTable(table), Encoding.UTF8));
                }

                sourceContext.AddSource(
                    "AutomaTable.Importer.Generated.TableImporterCatalog.g.cs",
                    SourceText.From(RenderCatalog(result.Tables), Encoding.UTF8));
            });
        }

        private static AnalysisResult Analyze(Compilation compilation)
        {
            var symbols = new List<INamedTypeSymbol>();
            foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
                CollectTableTypes(assembly.GlobalNamespace, symbols);

            var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
            var tables = ImmutableArray.CreateBuilder<TableModel>();
            foreach (var symbol in symbols.OrderBy(static value => value.ToDisplayString(), StringComparer.Ordinal))
            {
                var members = new List<MemberModel>();
                var failed = false;
                foreach (var property in symbol.GetMembers().OfType<IPropertySymbol>().Where(static property =>
                             !property.IsStatic && property.DeclaredAccessibility == Accessibility.Public))
                {
                    var conversion = AnalyzeType(property.Type);
                    if (conversion == null)
                    {
                        diagnostics.Add(Diagnostic.Create(
                            UnsupportedMemberType,
                            property.Locations.FirstOrDefault(),
                            property.Name,
                            symbol.ToDisplayString(),
                            property.Type.ToDisplayString()));
                        failed = true;
                        continue;
                    }

                    members.Add(new MemberModel(property.Name, conversion));
                }

                if (failed)
                    continue;

                var indexes = new List<IndexModel>();
                foreach (var attribute in symbol.GetAttributes())
                {
                    var attributeName = attribute.AttributeClass?.ToDisplayString();
                    var unique = attributeName == FindByAttributeName;
                    if (!unique && attributeName != FindAllByAttributeName)
                        continue;

                    var columns = ReadStringArray(attribute);
                    if (columns.Length == 0 || columns.Length == 1 && columns[0] == "Id")
                        continue;

                    indexes.Add(new IndexModel(columns, unique));
                }

                tables.Add(new TableModel(symbol.Name, members, indexes));
            }

            return new AnalysisResult(tables.ToImmutable(), diagnostics.ToImmutable());
        }

        private static void CollectTableTypes(INamespaceSymbol namespaceSymbol, List<INamedTypeSymbol> result)
        {
            foreach (var type in namespaceSymbol.GetTypeMembers())
                CollectTableType(type, result);
            foreach (var childNamespace in namespaceSymbol.GetNamespaceMembers())
                CollectTableTypes(childNamespace, result);
        }

        private static void CollectTableType(INamedTypeSymbol type, List<INamedTypeSymbol> result)
        {
            if (type.GetAttributes().Any(static attribute =>
                    attribute.AttributeClass?.ToDisplayString() == TableRowAttributeName))
            {
                result.Add(type);
            }

            foreach (var nested in type.GetTypeMembers())
                CollectTableType(nested, result);
        }

        private static ImmutableArray<string> ReadStringArray(AttributeData attribute)
        {
            if (attribute.ConstructorArguments.Length != 1 ||
                attribute.ConstructorArguments[0].Kind != TypedConstantKind.Array)
            {
                return ImmutableArray<string>.Empty;
            }

            return attribute.ConstructorArguments[0].Values
                .Where(static value => value.Value is string)
                .Select(static value => (string)value.Value!)
                .ToImmutableArray();
        }

        private static ConversionModel? AnalyzeType(ITypeSymbol type)
        {
            if (IsId(type))
                return new ConversionModel(StorageKind.Integer, ValueKind.Integer, null);

            if (type.TypeKind == TypeKind.Enum)
                return CreateEnumConversion((INamedTypeSymbol)type);

            if (IsNamedType(type, "AssetAddress", PrimitivesNamespace))
                return new ConversionModel(StorageKind.Text, ValueKind.String, null);

            switch (type.SpecialType)
            {
                case SpecialType.System_Boolean:
                case SpecialType.System_Byte:
                case SpecialType.System_SByte:
                case SpecialType.System_Int16:
                case SpecialType.System_UInt16:
                case SpecialType.System_Int32:
                case SpecialType.System_UInt32:
                case SpecialType.System_Int64:
                case SpecialType.System_UInt64:
                    return new ConversionModel(StorageKind.Integer, ValueKind.Integer, null);
                case SpecialType.System_Single:
                case SpecialType.System_Double:
                case SpecialType.System_Decimal:
                    return new ConversionModel(StorageKind.Real, ValueKind.Real, null);
                case SpecialType.System_String:
                    return new ConversionModel(
                        type.NullableAnnotation == NullableAnnotation.Annotated ? StorageKind.NullableText : StorageKind.Text,
                        ValueKind.String,
                        null);
            }

            if (IsReadOnlyMemoryOfByte(type))
                return new ConversionModel(StorageKind.Blob, ValueKind.Blob, null);

            if (type is INamedTypeSymbol nullable &&
                nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
                nullable.TypeArguments.Length == 1)
            {
                var underlying = nullable.TypeArguments[0];
                if (IsId(underlying))
                    return new ConversionModel(StorageKind.NullableInteger, ValueKind.Integer, null);
                if (IsNamedType(underlying, "AssetAddress", PrimitivesNamespace))
                    return new ConversionModel(StorageKind.NullableText, ValueKind.String, null);
                if (IsReadOnlyMemoryOfByte(underlying))
                    return new ConversionModel(StorageKind.NullableBlob, ValueKind.Blob, null);
            }

            var fullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (fullName == "global::System.DateTime")
                return new ConversionModel(StorageKind.Integer, ValueKind.DateTime, null);
            if (fullName == "global::System.DateTimeOffset")
                return new ConversionModel(StorageKind.Integer, ValueKind.DateTimeOffset, null);
            if (fullName == "global::System.TimeSpan")
                return new ConversionModel(StorageKind.Integer, ValueKind.TimeSpan, null);
            if (fullName == "global::System.Guid")
                return new ConversionModel(StorageKind.Text, ValueKind.Guid, null);

            return null;
        }

        private static ConversionModel CreateEnumConversion(INamedTypeSymbol type)
        {
            var values = type.GetMembers().OfType<IFieldSymbol>()
                .Where(static field => field.HasConstantValue)
                .Select(field => new EnumValue(
                    field.Name,
                    Convert.ToInt64(field.ConstantValue, CultureInfo.InvariantCulture)))
                .ToImmutableArray();
            return new ConversionModel(StorageKind.Integer, ValueKind.Enum, new EnumModel(type.Name, values));
        }

        private static bool IsId(ITypeSymbol type) =>
            type is INamedTypeSymbol named && named.IsGenericType && named.Name == "Id" && named.Arity == 1 &&
            named.ContainingNamespace.ToDisplayString() == PrimitivesNamespace;

        private static bool IsReadOnlyMemoryOfByte(ITypeSymbol type) =>
            type is INamedTypeSymbol named && named.IsGenericType && named.Name == "ReadOnlyMemory" && named.Arity == 1 &&
            named.ContainingNamespace.ToDisplayString() == "System" &&
            named.TypeArguments[0].SpecialType == SpecialType.System_Byte;

        private static bool IsNamedType(ITypeSymbol type, string name, string containingNamespace) =>
            type.Name == name && type.ContainingNamespace.ToDisplayString() == containingNamespace;

        private static string RenderCatalog(ImmutableArray<TableModel> tables)
        {
            var builder = new StringBuilder();
            builder.AppendLine("// <auto-generated />");
            builder.AppendLine("#nullable enable");
            builder.AppendLine();
            builder.AppendLine("namespace AutomaTable.Importer.Generated");
            builder.AppendLine("{");
            builder.AppendLine("    internal static class GeneratedTableImporterCatalog");
            builder.AppendLine("    {");
            builder.AppendLine("        public static global::System.Collections.Generic.IReadOnlyList<global::AutomaTable.Importer.Runtime.ITableSheetImporter> All { get; } =");
            builder.AppendLine("            new global::AutomaTable.Importer.Runtime.ITableSheetImporter[]");
            builder.AppendLine("            {");
            foreach (var table in tables)
                builder.Append("                new ").Append(table.RowName).AppendLine("SheetImporter(),");
            builder.AppendLine("            };");
            builder.AppendLine("    }");
            builder.AppendLine("}");
            return builder.ToString();
        }

        private static string RenderTable(TableModel table)
        {
            var builder = new StringBuilder();
            builder.AppendLine("// <auto-generated />");
            builder.AppendLine("#nullable enable");
            builder.AppendLine();
            builder.AppendLine("namespace AutomaTable.Importer.Generated");
            builder.AppendLine("{");
            builder.Append("    internal sealed class ").Append(table.RowName)
                .AppendLine("SheetImporter : global::AutomaTable.Importer.Runtime.ITableSheetImporter");
            builder.AppendLine("    {");
            builder.Append("        public string TableName => \"").Append(Escape(table.RowName)).AppendLine("\";");
            builder.Append("        public string CreateTableSql => \"")
                .Append(Escape(BuildCreateTableSql(table))).AppendLine("\";");
            RenderIndexes(builder, table);
            RenderImport(builder, table);
            RenderEnumReaders(builder, table);
            builder.AppendLine("    }");
            builder.AppendLine("}");
            return builder.ToString();
        }

        private static void RenderIndexes(StringBuilder builder, TableModel table)
        {
            builder.AppendLine("        public global::System.Collections.Generic.IReadOnlyList<string> CreateIndexSql { get; } =");
            builder.AppendLine("            new string[]");
            builder.AppendLine("            {");
            foreach (var index in table.Indexes)
            {
                builder.Append("                \"").Append(Escape(BuildCreateIndexSql(table, index))).AppendLine("\",");
            }
            builder.AppendLine("            };");
            builder.AppendLine();
        }

        private static void RenderImport(StringBuilder builder, TableModel table)
        {
            builder.AppendLine("        public int ImportRows(global::ExcelDataReader.IExcelDataReader reader, global::SQLite.SQLiteConnection connection)");
            builder.AppendLine("        {");
            builder.Append("            var columns = global::AutomaTable.Importer.Runtime.ExcelImportRuntime.ReadHeader(reader, TableName, new[] { ");
            for (var i = 0; i < table.Members.Count; i++)
            {
                if (i > 0) builder.Append(", ");
                builder.Append('"').Append(Escape(table.Members[i].Name)).Append('"');
            }
            builder.AppendLine(" });");
            builder.Append("            var statement = global::AutomaTable.Importer.Runtime.SqliteWriteRuntime.Prepare(connection.Handle, \"")
                .Append(Escape(BuildInsertSql(table))).AppendLine("\");");
            builder.AppendLine("            var importedRowCount = 0;");
            builder.AppendLine("            var rowNumber = 1;");
            builder.AppendLine("            try");
            builder.AppendLine("            {");
            builder.AppendLine("                while (reader.Read())");
            builder.AppendLine("                {");
            builder.AppendLine("                    rowNumber++;");
            builder.AppendLine("                    if (global::AutomaTable.Importer.Runtime.ExcelImportRuntime.IsBlankRow(reader, columns))");
            builder.AppendLine("                        continue;");
            for (var i = 0; i < table.Members.Count; i++)
                RenderBind(builder, table, table.Members[i], i);
            builder.AppendLine("                    global::AutomaTable.Importer.Runtime.SqliteWriteRuntime.Step(connection.Handle, statement);");
            builder.AppendLine("                    global::AutomaTable.Importer.Runtime.SqliteWriteRuntime.Reset(connection.Handle, statement);");
            builder.AppendLine("                    importedRowCount++;");
            builder.AppendLine("                }");
            builder.AppendLine("            }");
            builder.AppendLine("            finally");
            builder.AppendLine("            {");
            builder.AppendLine("                global::AutomaTable.Importer.Runtime.SqliteWriteRuntime.Finalize(statement);");
            builder.AppendLine("            }");
            builder.AppendLine("            return importedRowCount;");
            builder.AppendLine("        }");
            builder.AppendLine();
        }

        private static void RenderBind(StringBuilder builder, TableModel table, MemberModel member, int zeroBasedIndex)
        {
            var value = "reader.GetValue(columns[" + zeroBasedIndex.ToString(CultureInfo.InvariantCulture) + "])";
            var context = ", TableName, \"" + Escape(member.Name) + "\", rowNumber";
            string readerExpression;
            string bindMethod;
            switch (member.Conversion.ValueKind)
            {
                case ValueKind.Enum:
                    readerExpression = "Read" + member.Name + "(" + value + ", rowNumber)";
                    bindMethod = "BindInt64";
                    break;
                case ValueKind.Real:
                    readerExpression = "global::AutomaTable.Importer.Runtime.ExcelImportRuntime.ReadDouble(" + value + context + ")";
                    bindMethod = "BindDouble";
                    break;
                case ValueKind.String:
                    readerExpression = "global::AutomaTable.Importer.Runtime.ExcelImportRuntime." +
                        (member.Conversion.StorageKind == StorageKind.NullableText ? "ReadNullableString(" : "ReadString(") + value + context + ")";
                    bindMethod = "BindText";
                    break;
                case ValueKind.Blob:
                    var nullableBlob = member.Conversion.StorageKind == StorageKind.NullableBlob;
                    readerExpression = "global::AutomaTable.Importer.Runtime.ExcelImportRuntime." +
                        (nullableBlob ? "ReadNullableBlob(" : "ReadBlob(") + value + context + ")";
                    bindMethod = nullableBlob ? "BindNullableBlob" : "BindBlob";
                    break;
                case ValueKind.DateTime:
                    readerExpression = "global::AutomaTable.Importer.Runtime.ExcelImportRuntime.ReadDateTimeTicks(" + value + context + ")";
                    bindMethod = "BindInt64";
                    break;
                case ValueKind.DateTimeOffset:
                    readerExpression = "global::AutomaTable.Importer.Runtime.ExcelImportRuntime.ReadDateTimeOffsetTicks(" + value + context + ")";
                    bindMethod = "BindInt64";
                    break;
                case ValueKind.TimeSpan:
                    readerExpression = "global::AutomaTable.Importer.Runtime.ExcelImportRuntime.ReadTimeSpanTicks(" + value + context + ")";
                    bindMethod = "BindInt64";
                    break;
                case ValueKind.Guid:
                    readerExpression = "global::AutomaTable.Importer.Runtime.ExcelImportRuntime.ReadGuid(" + value + context + ")";
                    bindMethod = "BindText";
                    break;
                default:
                    var nullableInteger = member.Conversion.StorageKind == StorageKind.NullableInteger;
                    readerExpression = "global::AutomaTable.Importer.Runtime.ExcelImportRuntime." +
                        (nullableInteger ? "ReadNullableInt64(" : "ReadInt64(") + value + context + ")";
                    bindMethod = nullableInteger ? "BindNullableInt64" : "BindInt64";
                    break;
            }

            builder.Append("                    global::AutomaTable.Importer.Runtime.SqliteWriteRuntime.")
                .Append(bindMethod).Append("(connection.Handle, statement, ")
                .Append(zeroBasedIndex + 1).Append(", ").Append(readerExpression).AppendLine(");");
        }

        private static void RenderEnumReaders(StringBuilder builder, TableModel table)
        {
            foreach (var member in table.Members.Where(static member => member.Conversion.ValueKind == ValueKind.Enum))
            {
                var enumModel = member.Conversion.Enum!;
                builder.Append("        private long Read").Append(member.Name).AppendLine("(object? value, int rowNumber)");
                builder.AppendLine("        {");
                builder.AppendLine("            if (global::AutomaTable.Importer.Runtime.ExcelImportRuntime.TryReadEnumName(value, out var name))");
                builder.AppendLine("            {");
                builder.AppendLine("                switch (name)");
                builder.AppendLine("                {");
                foreach (var enumValue in enumModel.Values)
                {
                    builder.Append("                    case \"").Append(Escape(enumValue.Name)).Append("\": return ")
                        .Append(enumValue.Value.ToString(CultureInfo.InvariantCulture)).AppendLine("L;");
                }
                builder.AppendLine("                    default: throw global::AutomaTable.Importer.Runtime.ExcelImportRuntime.InvalidEnum(");
                builder.Append("                        TableName, \"").Append(Escape(member.Name)).Append("\", rowNumber, value, \"")
                    .Append(Escape(enumModel.Name)).AppendLine("\");");
                builder.AppendLine("                }");
                builder.AppendLine("            }");
                builder.Append("            return global::AutomaTable.Importer.Runtime.ExcelImportRuntime.ReadInt64(value, TableName, \"")
                    .Append(Escape(member.Name)).AppendLine("\", rowNumber);");
                builder.AppendLine("        }");
                builder.AppendLine();
            }
        }

        private static string BuildCreateTableSql(TableModel table)
        {
            var definitions = table.Members.Select(member =>
            {
                if (member.Name == "Id")
                    return QuoteIdentifier(member.Name) + " INTEGER PRIMARY KEY";

                var sqlType = member.Conversion.StorageKind switch
                {
                    StorageKind.Integer or StorageKind.NullableInteger => "INTEGER",
                    StorageKind.Real => "REAL",
                    StorageKind.Text or StorageKind.NullableText => "TEXT",
                    StorageKind.Blob or StorageKind.NullableBlob => "BLOB",
                    _ => throw new InvalidOperationException()
                };
                var nullable = member.Conversion.StorageKind is StorageKind.NullableInteger or StorageKind.NullableText or StorageKind.NullableBlob;
                return QuoteIdentifier(member.Name) + " " + sqlType + (nullable ? string.Empty : " NOT NULL");
            });
            return "CREATE TABLE " + QuoteIdentifier(table.RowName) + " (" + string.Join(", ", definitions) + ")";
        }

        private static string BuildCreateIndexSql(TableModel table, IndexModel index)
        {
            var name = "IX_" + table.RowName + "_" + string.Join("_", index.Columns);
            return "CREATE " + (index.Unique ? "UNIQUE " : string.Empty) + "INDEX " + QuoteIdentifier(name) +
                   " ON " + QuoteIdentifier(table.RowName) + " (" +
                   string.Join(", ", index.Columns.Select(QuoteIdentifier)) + ")";
        }

        private static string BuildInsertSql(TableModel table) =>
            "INSERT INTO " + QuoteIdentifier(table.RowName) + " (" +
            string.Join(", ", table.Members.Select(static member => QuoteIdentifier(member.Name))) + ") VALUES (" +
            string.Join(", ", Enumerable.Range(1, table.Members.Count).Select(static value => "?" + value)) + ")";

        private static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

        private sealed class AnalysisResult
        {
            public AnalysisResult(ImmutableArray<TableModel> tables, ImmutableArray<Diagnostic> diagnostics)
            {
                Tables = tables;
                Diagnostics = diagnostics;
            }

            public ImmutableArray<TableModel> Tables { get; }
            public ImmutableArray<Diagnostic> Diagnostics { get; }
        }

        private sealed class TableModel
        {
            public TableModel(string rowName, IReadOnlyList<MemberModel> members, IReadOnlyList<IndexModel> indexes)
            {
                RowName = rowName;
                Members = members;
                Indexes = indexes;
            }

            public string RowName { get; }
            public IReadOnlyList<MemberModel> Members { get; }
            public IReadOnlyList<IndexModel> Indexes { get; }
        }

        private sealed class MemberModel
        {
            public MemberModel(string name, ConversionModel conversion)
            {
                Name = name;
                Conversion = conversion;
            }

            public string Name { get; }
            public ConversionModel Conversion { get; }
        }

        private sealed class IndexModel
        {
            public IndexModel(ImmutableArray<string> columns, bool unique)
            {
                Columns = columns;
                Unique = unique;
            }

            public ImmutableArray<string> Columns { get; }
            public bool Unique { get; }
        }

        private sealed class ConversionModel
        {
            public ConversionModel(StorageKind storageKind, ValueKind valueKind, EnumModel? enumModel)
            {
                StorageKind = storageKind;
                ValueKind = valueKind;
                Enum = enumModel;
            }

            public StorageKind StorageKind { get; }
            public ValueKind ValueKind { get; }
            public EnumModel? Enum { get; }
        }

        private sealed class EnumModel
        {
            public EnumModel(string name, ImmutableArray<EnumValue> values)
            {
                Name = name;
                Values = values;
            }

            public string Name { get; }
            public ImmutableArray<EnumValue> Values { get; }
        }

        private sealed class EnumValue
        {
            public EnumValue(string name, long value)
            {
                Name = name;
                Value = value;
            }

            public string Name { get; }
            public long Value { get; }
        }

        private enum StorageKind
        {
            Integer,
            NullableInteger,
            Real,
            Text,
            NullableText,
            Blob,
            NullableBlob
        }

        private enum ValueKind
        {
            Integer,
            Real,
            String,
            Blob,
            Enum,
            DateTime,
            DateTimeOffset,
            TimeSpan,
            Guid
        }
    }
}
