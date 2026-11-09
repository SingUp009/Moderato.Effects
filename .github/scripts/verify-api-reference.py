import pathlib
import re
import sys

output = pathlib.Path(sys.argv[1])
root = pathlib.Path(__file__).resolve().parents[2]
expected = set()
for api in root.glob("src/*/PublicAPI.*.txt"):
    for line in api.read_text().splitlines():
        if re.fullmatch(r"Moderato\.Effects(?:\.\w+)+(?:<[^<>]+>)?", line):
            name = re.sub(r"<([^<>]+)>", lambda match: "-" + str(match[1].count(",") + 1), line)
            expected.add(name.lower() + ".md")

assert expected, "No public API types found"
assert all((output / page).is_file() for page in expected), expected - {page.name for page in output.glob("*.md")}
for page in output.glob("*.md"):
    for link in re.findall(r"\]\(([^)]+)\)", page.read_text()):
        if not link.startswith(("https://", "http://", "#")):
            assert (output / (link.split("#")[0] + ".md")).is_file(), (page.name, link)

effect = (output / "moderato.effects.effect-3.md").read_text()
assert "public readonly struct Effect<T, E, R>" in effect
assert "CancellationToken cancellationToken = default(CancellationToken)" in effect
assert 'public const string SUPPRESSED_DATA_KEY = "Moderato.Effects.Suppressed"' in effect
assert "needs `R` and ends with `T` or `E`" in effect
assert "where F : IUnionRest<F, E, TCase>" in effect
assert "IEquatable<Exit<T, E>>" in (output / "moderato.effects.exit-2.md").read_text()
assert "**Returns:**" in (output / "moderato.effects.http.effectrequest.md").read_text()
assert "Connects effects to request cancellation and standard logging." in (output / "moderato.effects.http.effectshttpservicecollectionextensions.md").read_text()
print(f"Verified {len(expected)} tracked public types, local links, signatures, and XML documentation.")
