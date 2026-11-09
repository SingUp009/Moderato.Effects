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
            expected.add(name + ".md")

assert expected, "No public API types found"
actual = {page.name for page in output.glob("*.md")}
assert expected <= actual, expected - actual
for page in output.glob("*.md"):
    for link in re.findall(r"\]\(([^)]+)\)", page.read_text()):
        if not link.startswith(("https://", "http://", "#")):
            assert (output / (link.split("#")[0] + ".md")).is_file(), (page.name, link)

effect = (output / "Moderato.Effects.Effect-3.md").read_text()
assert "public readonly struct Effect<T, E, R>" in effect
assert "CancellationToken cancellationToken = default(CancellationToken)" in effect
assert 'public const string SUPPRESSED_DATA_KEY = "Moderato.Effects.Suppressed"' in effect
assert "needs `R` and ends with `T` or `E`" in effect
assert "where F : IUnionRest<F, E, TCase>" in effect
assert "IEquatable<Exit<T, E>>" in (output / "Moderato.Effects.Exit-2.md").read_text()
assert "ConstraintKind()" not in (output / "Moderato.Effects.EntityFrameworkCore.ConstraintKind.md").read_text()
delegate = (output / "Moderato.Effects.EntityFrameworkCore.TryMapDatabaseFailure-1.md").read_text()
assert "public delegate bool TryMapDatabaseFailure<E>(DatabaseFailure failure, out E error)" in delegate
assert "IntPtr" not in delegate
assert "**Returns:**" in (output / "Moderato.Effects.Http.EffectRequest.md").read_text()
assert "Connects effects to request cancellation and standard logging." in (output / "Moderato.Effects.Http.EffectsHttpServiceCollectionExtensions.md").read_text()
print(f"Verified {len(expected)} tracked public types, local links, signatures, and XML documentation.")
