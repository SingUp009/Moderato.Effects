using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

if (args.Length != 1)
    throw new ArgumentException("Usage: ApiReference <output-directory>");

var output = Path.GetFullPath(args[0]);
if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
    throw new IOException("Use an empty output directory so removed APIs cannot leave stale pages.");
Directory.CreateDirectory(output);
var packages = new[] { "Moderato.Effects", "Moderato.Effects.Hosting", "Moderato.Effects.Http", "Moderato.Effects.EntityFrameworkCore" };
var paths = Directory.GetFiles(AppContext.BaseDirectory, "*.dll")
    .Concat(((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator))
    .Distinct(StringComparer.Ordinal).ToArray();
var references = paths.Select(path => MetadataReference.CreateFromFile(path)).ToArray();
var compilation = CSharpCompilation.Create("ApiReference", references: references);
var format = new SymbolDisplayFormat(
    typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
    genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeTypeConstraints | SymbolDisplayGenericsOptions.IncludeVariance,
    memberOptions: SymbolDisplayMemberOptions.IncludeAccessibility | SymbolDisplayMemberOptions.IncludeModifiers | SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeConstantValue | SymbolDisplayMemberOptions.IncludeRef,
    parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName | SymbolDisplayParameterOptions.IncludeDefaultValue | SymbolDisplayParameterOptions.IncludeParamsRefOut | SymbolDisplayParameterOptions.IncludeExtensionThis,
    propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
    kindOptions: SymbolDisplayKindOptions.IncludeTypeKeyword,
    miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);
var assemblies = packages.Select(package => (IAssemblySymbol)compilation.GetAssemblyOrModuleSymbol(
    references.Single(reference => Path.GetFileNameWithoutExtension(reference.FilePath) == package))!).ToArray();
var types = assemblies.SelectMany(assembly => GetTypes(assembly.GlobalNamespace)).ToArray();
var documents = packages.SelectMany(package => XDocument.Load(Path.Combine(AppContext.BaseDirectory, package + ".xml"))
    .Descendants("member")).ToDictionary(member => (string)member.Attribute("name")!, StringComparer.Ordinal);

foreach (var assembly in assemblies)
{
    var index = new StringBuilder($"# {assembly.Name}\n\n");
    foreach (var type in types.Where(type => SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, assembly)).OrderBy(PageName, StringComparer.Ordinal))
    {
        index.AppendLine($"- [`{Label(type)}`]({PageName(type)})");
        var page = new StringBuilder($"# `{Label(type)}`\n\nNamespace: `{type.ContainingNamespace}`\n\n");
        WriteDocumentation(page, type);
        var modifier = type.IsStatic ? "static " : type.TypeKind == TypeKind.Class && type.IsSealed ? "sealed " : type.TypeKind == TypeKind.Class && type.IsAbstract ? "abstract " : "";
        page.AppendLine($"```csharp\npublic {modifier}{type.ToDisplayString(format)}\n```\n");
        if (type.BaseType is { SpecialType: SpecialType.None } baseType)
            page.AppendLine($"Base type: {Link(baseType)}\n");
        if (!type.Interfaces.IsEmpty)
            page.AppendLine($"Implements: {string.Join(", ", type.Interfaces.Select(Link))}\n");
        foreach (var member in type.GetMembers().Where(IsPublicMember).OrderBy(member => member.MetadataName, StringComparer.Ordinal).ThenBy(member => member.ToDisplayString(format), StringComparer.Ordinal))
        {
            var name = member is IMethodSymbol { MethodKind: MethodKind.Constructor } ? type.Name : member.Name;
            page.AppendLine($"## `{name}`\n\n```csharp\n{member.ToDisplayString(format)}\n```\n");
            WriteDocumentation(page, member);
        }
        File.WriteAllText(Path.Combine(output, PageName(type) + ".md"), page.ToString());
    }
    File.WriteAllText(Path.Combine(output, assembly.Name + ".md"), index.ToString());
}

var home = new StringBuilder("# API Reference\n\n[Guides and tutorials](https://moderato-effects.009singup.workers.dev) explain how to use the library. This wiki lists the public types and members of the four packages, using compiler-rendered signatures and their XML documentation.\n\nThe reference describes the compiled API, including generated union members. Signatures use short type names; the namespace is shown on each type page.\n\n## Packages\n\n");
foreach (var assembly in assemblies)
{
    var version = assembly.GetAttributes().Single(attribute => attribute.AttributeClass?.Name == "AssemblyInformationalVersionAttribute").ConstructorArguments[0].Value;
    home.AppendLine($"- [{assembly.Name}]({assembly.Name}) — `{version}`");
}
File.WriteAllText(Path.Combine(output, "Home.md"), home.ToString());
File.WriteAllText(Path.Combine(output, "_Sidebar.md"), "[API Reference](Home)\n\n" + string.Join("\n", packages.Select(package => $"- [{package}]({package})")) + "\n\n[Guides and tutorials](https://moderato-effects.009singup.workers.dev)\n");
Console.WriteLine($"Generated {types.Length} public type pages for {assemblies.Length} packages in {output}.");

IEnumerable<INamedTypeSymbol> GetTypes(INamespaceOrTypeSymbol container)
{
    foreach (var member in container.GetMembers())
    {
        if (member is INamespaceSymbol childNamespace)
        {
            foreach (var type in GetTypes(childNamespace))
                yield return type;
        }

        if (member is INamedTypeSymbol { DeclaredAccessibility: Accessibility.Public } childType)
        {
            yield return childType;
            foreach (var type in GetTypes(childType))
                yield return type;
        }
    }
}

bool IsPublicMember(ISymbol member) => member.DeclaredAccessibility == Accessibility.Public && member is not INamedTypeSymbol &&
    (member is not IMethodSymbol method || method.MethodKind is MethodKind.Constructor or MethodKind.Ordinary or MethodKind.Conversion or MethodKind.UserDefinedOperator) &&
    (member.CanBeReferencedByName || member is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.Conversion or MethodKind.UserDefinedOperator });

string PageName(INamedTypeSymbol type) => type.GetDocumentationCommentId()![2..].Replace('`', '-').ToLowerInvariant();
string Label(ISymbol symbol) => symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

string Link(ISymbol symbol)
{
    var type = symbol as INamedTypeSymbol ?? symbol.ContainingType;
    return type is not null && packages.Contains(type.ContainingAssembly.Name, StringComparer.Ordinal)
        ? $"[`{Label(symbol)}`]({PageName(type.OriginalDefinition)})"
        : $"`{Label(symbol)}`";
}

string Render(XNode node) => node switch
{
    XText text => Regex.Replace(text.Value, @"\s+", " "),
    XElement { Name.LocalName: "paramref" or "typeparamref" } element => $"`{element.Attribute("name")!.Value}`",
    XElement { Name.LocalName: "c" } element => $"`{element.Value}`",
    XElement { Name.LocalName: "see" } element when element.Attribute("langword") is { } word => $"`{word.Value}`",
    XElement { Name.LocalName: "see" } element => DocumentationCommentId.GetFirstSymbolForDeclarationId(element.Attribute("cref")!.Value, compilation) is { } symbol ? Link(symbol) : throw new InvalidOperationException($"Unresolved XML reference: {element}"),
    XElement element => string.Concat(element.Nodes().Select(Render)),
    _ => throw new InvalidOperationException($"Unsupported XML documentation: {node}")
};

void WriteDocumentation(StringBuilder page, ISymbol symbol)
{
    if (symbol.GetDocumentationCommentId() is not { } id || !documents.TryGetValue(id, out var document))
        return;
    foreach (var element in document.Elements())
    {
        var label = element.Name.LocalName switch
        {
            "param" or "typeparam" => $"**`{element.Attribute("name")!.Value}`:** ",
            "returns" => "**Returns:** ",
            "remarks" => "**Remarks:** ",
            "exception" => $"**Throws `{element.Attribute("cref")!.Value[2..]}`:** ",
            "summary" => "",
            _ => throw new InvalidOperationException($"Unsupported XML documentation: {element.Name}")
        };
        page.AppendLine(label + Render(element).Trim() + "\n");
    }
}
