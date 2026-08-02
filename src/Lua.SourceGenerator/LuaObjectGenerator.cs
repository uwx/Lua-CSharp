using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Lua.SourceGenerator;

[Generator(LanguageNames.CSharp)]
public partial class LuaObjectGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var provider = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                "Lua.LuaObjectAttribute",
                static (node, cancellation) =>
                {
                    return node is ClassDeclarationSyntax or RecordDeclarationSyntax;
                },
                static (context, cancellation) =>
                {
                    return context;
                }
            )
            .Combine(context.CompilationProvider)
            .WithComparer(Comparer.Instance);

        // Read LuaCATS output directory from MSBuild property (optional).
        // When unset, no .d.lua file is emitted.
        var luaCATSOutputDir = context.AnalyzerConfigOptionsProvider
            .Select((configOptions, token) =>
            {
                var isDesignTimeBuild =
                    configOptions.GlobalOptions.TryGetValue(
                        "build_property.DesignTimeBuild", out var dtb)
                    && dtb == "true";

                if (isDesignTimeBuild)
                    return (string?)null;

                if (configOptions.GlobalOptions.TryGetValue(
                        "build_property.LuaSourceGenerator_LuaCATSOutputDirectory",
                        out var path))
                {
                    return path;
                }

                return (string?)null;
            });

        var combined = provider.Collect().Combine(luaCATSOutputDir);

        context.RegisterSourceOutput(
            context.CompilationProvider.Combine(combined),
            (sourceProductionContext, t) =>
            {
                var (compilation, (list, luaCATSDir)) = t;
                var references = SymbolReferences.Create(compilation);
                if (references == null)
                    return;

                var builder = new CodeBuilder();

                var metaDict = new Dictionary<INamedTypeSymbol, TypeMetadata>(
                    SymbolEqualityComparer.Default
                );

                foreach (var (x, _) in list)
                {
                    var symbol = (INamedTypeSymbol)x.TargetSymbol;
                    var typeMeta = new TypeMetadata(
                        (TypeDeclarationSyntax)x.TargetNode,
                        symbol,
                        references
                    );
                    metaDict.Add(symbol, typeMeta);
                }

                var tempCollections = new TempCollections();
                foreach (var pair in metaDict)
                {
                    var typeMeta = pair.Value;
                    if (
                        TryEmit(
                            typeMeta,
                            builder,
                            references,
                            compilation,
                            in sourceProductionContext,
                            metaDict,
                            tempCollections
                        )
                    )
                    {
                        var fullType = typeMeta
                            .FullTypeName.Replace("global::", "")
                            .Replace("<", "_")
                            .Replace(">", "_");

                        sourceProductionContext.AddSource(
                            $"{fullType}.LuaObject.g.cs",
                            builder.ToString()
                        );
                    }

                    tempCollections.Clear();
                    builder.Clear();
                }

                // Emit LuaCATS .d.lua file if output directory is configured
                if (!string.IsNullOrEmpty(luaCATSDir) && metaDict.Count > 0)
                {
                    var asmName = compilation.AssemblyName;
                    if (!string.IsNullOrEmpty(asmName))
                    {
                        TryEmitLuaCATS(
                            metaDict,
                            references,
                            compilation,
                            luaCATSDir!,
                            asmName!,
                            in sourceProductionContext
                        );
                    }
                }
            }
        );
    }
}
