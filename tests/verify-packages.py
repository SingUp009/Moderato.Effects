"""Verify the Core, Hosting and HTTP artifacts produced by CI before consumer tests."""

import sys
import xml.etree.ElementTree as ET
from pathlib import Path
from zipfile import ZipFile

root, version, commit = sys.argv[1:]
root = Path(root)
ns = {"n": "http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"}

for package in ("Moderato.Effects", "Moderato.Effects.Hosting", "Moderato.Effects.Http"):
    with ZipFile(root / f"{package}.{version}.nupkg") as archive:
        metadata = ET.fromstring(archive.read(f"{package}.nuspec")).find("n:metadata", ns)
        assert metadata is not None, package
        assert metadata.findtext("n:id", namespaces=ns) == package
        assert metadata.findtext("n:version", namespaces=ns) == version
        assert metadata.findtext("n:authors", namespaces=ns) == "SingUp_009"
        assert metadata.findtext("n:license", namespaces=ns) == "MIT"
        assert metadata.find("n:repository", ns).get("commit") == commit
        assert metadata.findtext("n:readme", namespaces=ns) == "README.md"
        assert archive.read("README.md")
        assert archive.read(f"lib/net11.0/{package}.dll")
        assert archive.read(f"lib/net11.0/{package}.xml")
        analyzers = [name for name in archive.namelist() if name.startswith("analyzers/")]
        if package == "Moderato.Effects":
            assert "analyzers/dotnet/cs/Moderato.Effects.SourceGenerator.dll" in analyzers
        else:
            assert not analyzers, analyzers
            dependencies = {item.get("id"): item.get("version") for item in metadata.findall(".//n:dependency", ns)}
            assert dependencies["Moderato.Effects"] in (version, f"[{version},)")
            if package == "Moderato.Effects.Http":
                assert set(dependencies) == {"Moderato.Effects"}, dependencies
                frameworks = metadata.findall(".//n:frameworkReference", ns)
                assert [item.get("name") for item in frameworks] == ["Microsoft.AspNetCore.App"]
            for name in (("Microsoft.Extensions.Logging",) if package == "Moderato.Effects.Hosting" else ()):
                lower = "11.0.0-rc.3.26522.148"
                assert dependencies[name] in (lower, f"[{lower},)"), dependencies[name]
    with ZipFile(root / f"{package}.{version}.snupkg") as symbols:
        pdb = symbols.read(f"lib/net11.0/{package}.pdb")
        source = f"https://raw.githubusercontent.com/SingUp009/Moderato.Effects/{commit}/".encode()
        assert source in pdb, f"SourceLink does not identify {commit} in {package}"
    print(f"verified {package} {version}: metadata, payload, symbols and SourceLink {commit}")
