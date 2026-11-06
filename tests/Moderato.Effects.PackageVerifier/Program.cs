using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: Moderato.Effects.PackageVerifier <artifacts> <version> <commit>");
    return 1;
}

var root = args[0];
var version = args[1];
var commit = args[2];
XNamespace ns = "http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd";
var source = Encoding.UTF8.GetBytes($"https://raw.githubusercontent.com/SingUp009/Moderato.Effects/{commit}/");

try
{
    foreach (var package in new[] { "Moderato.Effects", "Moderato.Effects.Hosting", "Moderato.Effects.Http", "Moderato.Effects.EntityFrameworkCore" })
    {
        using var archive = ZipFile.OpenRead(Path.Combine(root, $"{package}.{version}.nupkg"));
        using var nuspec = archive.GetEntry($"{package}.nuspec")?.Open()
            ?? throw new InvalidDataException($"{package}: missing nuspec.");
        var metadata = XDocument.Load(nuspec).Root?.Element(ns + "metadata")
            ?? throw new InvalidDataException($"{package}: missing nuspec metadata.");
        Check((string?)metadata.Element(ns + "id") == package, $"{package}: unexpected package ID.");
        Check((string?)metadata.Element(ns + "version") == version, $"{package}: unexpected version.");
        Check((string?)metadata.Element(ns + "authors") == "SingUp_009", $"{package}: unexpected authors.");
        Check((string?)metadata.Element(ns + "license") == "MIT", $"{package}: unexpected license.");
        Check((string?)metadata.Element(ns + "repository")?.Attribute("commit") == commit, $"{package}: unexpected repository commit.");
        Check((string?)metadata.Element(ns + "readme") == "README.md", $"{package}: unexpected README metadata.");
        Check(Read(archive, "README.md").Length > 0, $"{package}: empty README.");
        Check(Read(archive, $"lib/net11.0/{package}.dll").Length > 0, $"{package}: empty assembly.");
        Check(Read(archive, $"lib/net11.0/{package}.xml").Length > 0, $"{package}: empty XML documentation.");
        var analyzers = archive.Entries.Where(entry => entry.FullName.StartsWith("analyzers/", StringComparison.Ordinal)).ToArray();
        if (package == "Moderato.Effects")
        {
            Check(analyzers.Any(entry => entry.FullName == "analyzers/dotnet/cs/Moderato.Effects.SourceGenerator.dll"), $"{package}: missing generator/analyzers.");
        }
        else
        {
            Check(analyzers.Length == 0, $"{package}: duplicates generator/analyzers.");
            var dependencies = metadata.Descendants(ns + "dependency").ToDictionary(
                item => (string?)item.Attribute("id") ?? throw new InvalidDataException($"{package}: dependency without ID."),
                item => (string?)item.Attribute("version"));
            Check(dependencies.TryGetValue("Moderato.Effects", out var coreVersion) && IsMinimum(coreVersion, version), $"{package}: unexpected Core dependency.");
            if (package == "Moderato.Effects.Http")
            {
                Check(dependencies.Count == 1, $"{package}: unexpected package dependencies.");
                var frameworks = metadata.Descendants(ns + "frameworkReference").Select(item => (string?)item.Attribute("name"));
                Check(frameworks.SequenceEqual(["Microsoft.AspNetCore.App"]), $"{package}: unexpected framework references.");
            }
            else if (package == "Moderato.Effects.Hosting")
            {
                Check(dependencies.TryGetValue("Microsoft.Extensions.Logging", out var loggingVersion)
                    && IsMinimum(loggingVersion, "11.0.0-rc.3.26522.148"), $"{package}: unexpected logging dependency.");
            }
            else
            {
                Check(dependencies.Count == 3, $"{package}: unexpected package dependencies.");
                Check(dependencies.TryGetValue("Microsoft.EntityFrameworkCore.Relational", out var efVersion)
                    && IsMinimum(efVersion, "10.0.9"), $"{package}: unexpected EF dependency.");
                Check(dependencies.TryGetValue("Microsoft.Data.Sqlite.Core", out var sqliteVersion)
                    && IsMinimum(sqliteVersion, "10.0.9"), $"{package}: unexpected SQLite dependency.");
            }
        }

        using var symbols = ZipFile.OpenRead(Path.Combine(root, $"{package}.{version}.snupkg"));
        var pdb = Read(symbols, $"lib/net11.0/{package}.pdb");
        Check(pdb.AsSpan().IndexOf(source) >= 0, $"SourceLink does not identify {commit} in {package}");
        Console.WriteLine($"verified {package} {version}: metadata, payload, symbols and SourceLink {commit}");
    }
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static bool IsMinimum(string? actual, string expected) => actual == expected || actual == $"[{expected},)";

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}

static byte[] Read(ZipArchive archive, string path)
{
    var entry = archive.GetEntry(path) ?? throw new InvalidDataException($"Missing package entry: {path}");
    using var stream = entry.Open();
    var bytes = new byte[checked((int)entry.Length)];
    stream.ReadExactly(bytes);
    return bytes;
}
