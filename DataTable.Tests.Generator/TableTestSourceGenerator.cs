using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace DataTable.Tests.Generator
{
    [Generator]
    public sealed class TableTestSourceGenerator : IIncrementalGenerator
    {
        private const string TableRowAttributeName = "DataTable.Annotations.TableRowAttribute";
        private const string FindByAttributeName = "DataTable.Annotations.FindByAttribute";
        private const string IdNamespace = "DataTable.Primitives";

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var tables = context.CompilationProvider.Select(static (compilation, _) => Analyze(compilation));
            context.RegisterSourceOutput(tables, static (sourceContext, models) =>
            {
                foreach (var model in models)
                {
                    sourceContext.AddSource(
                        "DataTable.Tests.Generated." + model.RowName + "ValidationTests.g.cs",
                        SourceText.From(Render(model), Encoding.UTF8));
                }
            });
        }

        private static ImmutableArray<TableTestModel> Analyze(Compilation compilation)
        {
            var tableSymbols = new List<INamedTypeSymbol>();
            foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
            {
                CollectTableTypes(assembly.GlobalNamespace, tableSymbols);
            }

            var tableSet = new HashSet<INamedTypeSymbol>(tableSymbols, SymbolEqualityComparer.Default);
            var models = ImmutableArray.CreateBuilder<TableTestModel>();
            foreach (var table in tableSymbols.OrderBy(static value => value.ToDisplayString(), StringComparer.Ordinal))
            {
                var uniqueFinders = new List<ImmutableArray<string>>();
                foreach (var attribute in table.GetAttributes())
                {
                    if (attribute.AttributeClass?.ToDisplayString() != FindByAttributeName)
                        continue;

                    var keys = ReadStringArray(attribute);
                    if (keys.Length == 1 && keys[0] == "Id")
                        continue;
                    if (keys.Length > 0 && !uniqueFinders.Any(existing => existing.SequenceEqual(keys)))
                        uniqueFinders.Add(keys);
                }

                var assets = new List<NullableMember>();
                var references = new List<ReferenceMember>();
                foreach (var property in table.GetMembers().OfType<IPropertySymbol>())
                {
                    var memberType = property.Type;
                    var nullable = TryUnwrapNullable(memberType, out var underlyingType);

                    if (IsNamedType(underlyingType, "AssetAddress", IdNamespace))
                    {
                        assets.Add(new NullableMember(property.Name, nullable));
                        continue;
                    }

                    if (underlyingType is INamedTypeSymbol idType &&
                        idType.IsGenericType && idType.Name == "Id" && idType.Arity == 1 &&
                        idType.ContainingNamespace.ToDisplayString() == IdNamespace &&
                        idType.TypeArguments[0] is INamedTypeSymbol targetTable &&
                        !SymbolEqualityComparer.Default.Equals(targetTable, table) &&
                        tableSet.Contains(targetTable))
                    {
                        references.Add(new ReferenceMember(property.Name, targetTable.Name, nullable));
                    }
                }

                models.Add(new TableTestModel(
                    table.Name,
                    uniqueFinders,
                    assets,
                    references));
            }

            return models.ToImmutable();
        }

        private static void CollectTableTypes(INamespaceSymbol namespaceSymbol, List<INamedTypeSymbol> result)
        {
            foreach (var type in namespaceSymbol.GetTypeMembers())
            {
                CollectTableType(type, result);
            }

            foreach (var childNamespace in namespaceSymbol.GetNamespaceMembers())
            {
                CollectTableTypes(childNamespace, result);
            }
        }

        private static void CollectTableType(INamedTypeSymbol type, List<INamedTypeSymbol> result)
        {
            if (HasAttribute(type, TableRowAttributeName))
                result.Add(type);

            foreach (var nested in type.GetTypeMembers())
                CollectTableType(nested, result);
        }

        private static bool HasAttribute(INamedTypeSymbol type, string attributeName)
        {
            return type.GetAttributes().Any(attribute =>
                attribute.AttributeClass?.ToDisplayString() == attributeName);
        }

        private static ImmutableArray<string> ReadStringArray(AttributeData attribute)
        {
            if (attribute.ConstructorArguments.Length != 1)
                return ImmutableArray<string>.Empty;

            var argument = attribute.ConstructorArguments[0];
            if (argument.Kind != TypedConstantKind.Array)
                return ImmutableArray<string>.Empty;

            return argument.Values
                .Where(static value => value.Value is string)
                .Select(static value => (string)value.Value!)
                .ToImmutableArray();
        }

        private static bool TryUnwrapNullable(ITypeSymbol type, out ITypeSymbol underlyingType)
        {
            if (type is INamedTypeSymbol nullable &&
                nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
                nullable.TypeArguments.Length == 1)
            {
                underlyingType = nullable.TypeArguments[0];
                return true;
            }

            underlyingType = type;
            return false;
        }

        private static bool IsNamedType(ITypeSymbol type, string name, string containingNamespace)
        {
            return type.Name == name && type.ContainingNamespace.ToDisplayString() == containingNamespace;
        }

        private static string Render(TableTestModel model)
        {
            var builder = new StringBuilder();
            builder.AppendLine("// <auto-generated />");
            builder.AppendLine("#nullable enable");
            builder.AppendLine();
            builder.AppendLine("namespace DataTable.Tests.Generated");
            builder.AppendLine("{");
            builder.AppendLine("    [global::NUnit.Framework.TestFixture]");
            builder.Append("    public sealed class ").Append(model.RowName).AppendLine("ValidationTests");
            builder.AppendLine("    {");
            RenderUniqueTest(builder, "Id_IsUnique", model.RowName, new[] { "Id" });

            foreach (var finder in model.UniqueFinders)
            {
                RenderUniqueTest(
                    builder,
                    "FindBy" + string.Join("And", finder) + "_IsUnique",
                    model.RowName,
                    finder);
            }

            foreach (var asset in model.Assets)
            {
                builder.AppendLine("        [global::NUnit.Framework.Test]");
                builder.Append("        public void ").Append(asset.Name).AppendLine("_ResourceExists()");
                builder.AppendLine("        {");
                builder.AppendLine("            global::DataTable.Tests.Infrastructure.TableDataAssertions.AssertAssetAddressExists(");
                builder.Append("                \"").Append(Escape(model.RowName)).AppendLine("\",");
                builder.AppendLine("                \"Id\",");
                builder.Append("                \"").Append(Escape(asset.Name)).AppendLine("\",");
                builder.Append("                ").Append(asset.Nullable ? "true" : "false").AppendLine(");");
                builder.AppendLine("        }");
                builder.AppendLine();
            }

            foreach (var reference in model.References)
            {
                builder.AppendLine("        [global::NUnit.Framework.Test]");
                builder.Append("        public void ").Append(reference.Name).Append("_References_")
                    .Append(reference.TargetRowName).AppendLine("()");
                builder.AppendLine("        {");
                builder.AppendLine("            global::DataTable.Tests.Infrastructure.TableDataAssertions.AssertReferenceExists(");
                builder.Append("                \"").Append(Escape(model.RowName)).AppendLine("\",");
                builder.AppendLine("                \"Id\",");
                builder.Append("                \"").Append(Escape(reference.Name)).AppendLine("\",");
                builder.Append("                \"").Append(Escape(reference.TargetRowName)).AppendLine("\",");
                builder.AppendLine("                \"Id\",");
                builder.Append("                ").Append(reference.Nullable ? "true" : "false").AppendLine(");");
                builder.AppendLine("        }");
                builder.AppendLine();
            }

            builder.AppendLine("    }");
            builder.AppendLine("}");
            return builder.ToString();
        }

        private static void RenderUniqueTest(
            StringBuilder builder,
            string methodName,
            string tableName,
            IReadOnlyList<string> columns)
        {
            builder.AppendLine("        [global::NUnit.Framework.Test]");
            builder.Append("        public void ").Append(methodName).AppendLine("()");
            builder.AppendLine("        {");
            builder.AppendLine("            global::DataTable.Tests.Infrastructure.TableDataAssertions.AssertUnique(");
            builder.Append("                \"").Append(Escape(tableName)).AppendLine("\",");
            builder.Append("                new[] { ");
            for (var i = 0; i < columns.Count; i++)
            {
                if (i > 0) builder.Append(", ");
                builder.Append('"').Append(Escape(columns[i])).Append('"');
            }
            builder.AppendLine(" });");
            builder.AppendLine("        }");
            builder.AppendLine();
        }

        private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

        private sealed class TableTestModel
        {
            public TableTestModel(
                string rowName,
                IReadOnlyList<ImmutableArray<string>> uniqueFinders,
                IReadOnlyList<NullableMember> assets,
                IReadOnlyList<ReferenceMember> references)
            {
                RowName = rowName;
                UniqueFinders = uniqueFinders;
                Assets = assets;
                References = references;
            }

            public string RowName { get; }
            public IReadOnlyList<ImmutableArray<string>> UniqueFinders { get; }
            public IReadOnlyList<NullableMember> Assets { get; }
            public IReadOnlyList<ReferenceMember> References { get; }
        }

        private class NullableMember
        {
            public NullableMember(string name, bool nullable)
            {
                Name = name;
                Nullable = nullable;
            }

            public string Name { get; }
            public bool Nullable { get; }
        }

        private sealed class ReferenceMember : NullableMember
        {
            public ReferenceMember(string name, string targetRowName, bool nullable)
                : base(name, nullable)
            {
                TargetRowName = targetRowName;
            }

            public string TargetRowName { get; }
        }
    }
}
