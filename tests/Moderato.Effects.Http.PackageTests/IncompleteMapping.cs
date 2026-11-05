using Microsoft.AspNetCore.Http;
using Moderato.Effects;

public sealed record InvalidInput(string Reason);

[Error]
public partial union ExpandedError(Missing, InvalidInput);

public static class IncompleteMapping
{
    public static IResult Map(ExpandedError error) => error switch
    {
        Missing missing => TypedResults.NotFound(missing),
    };
}
