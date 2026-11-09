import pathlib
import re
import sys
from urllib.parse import unquote

output = pathlib.Path(sys.argv[1])
root = pathlib.Path(__file__).resolve().parents[2]
expected = set()
for api in root.glob("src/*/PublicAPI.*.txt"):
    for line in api.read_text().splitlines():
        if re.fullmatch(r"Moderato\.Effects(?:\.\w+)+(?:<[^<>]+>)?", line):
            name = line.replace("<", "[").replace(">", "]")
            expected.add(name + ".md")

assert expected, "No public API types found"
actual = {page.name for page in output.glob("*.md")}
assert expected <= actual, expected - actual
for page in output.glob("*.md"):
    for link in re.findall(r"\]\(([^)]+)\)", page.read_text()):
        if not link.startswith(("https://", "http://", "#")):
            assert not any(char in link for char in " [],"), (page.name, link)
            assert (output / (unquote(link.split("#")[0]) + ".md")).is_file(), (page.name, link)

effect = (output / "Moderato.Effects.Effect[T, E, R].md").read_text()
assert "public readonly struct Effect<T, E, R>" in effect
assert "CancellationToken cancellationToken = default(CancellationToken)" in effect
assert 'public const string SUPPRESSED_DATA_KEY = "Moderato.Effects.Suppressed"' in effect
assert "needs `R` and ends with `T` or `E`" in effect
assert "where F : IUnionRest<F, E, TCase>" in effect
assert "IEquatable<Exit<T, E>>" in (output / "Moderato.Effects.Exit[T, E].md").read_text()
assert "ConstraintKind()" not in (output / "Moderato.Effects.EntityFrameworkCore.ConstraintKind.md").read_text()
delegate = (output / "Moderato.Effects.EntityFrameworkCore.TryMapDatabaseFailure[E].md").read_text()
assert "public delegate bool TryMapDatabaseFailure<E>(DatabaseFailure failure, out E error)" in delegate
assert "IntPtr" not in delegate
assert "**Returns:**" in (output / "Moderato.Effects.Http.EffectRequest.md").read_text()
assert "Connects effects to request cancellation and standard logging." in (output / "Moderato.Effects.Http.EffectsHttpServiceCollectionExtensions.md").read_text()
print(f"Verified {len(expected)} tracked public types, local links, signatures, and XML documentation.")
