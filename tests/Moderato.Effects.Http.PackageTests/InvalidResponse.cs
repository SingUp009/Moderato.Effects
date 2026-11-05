using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

public static class InvalidResponse
{
    public static Results<Ok<int>, NotFound<Missing>> Map()
        => TypedResults.BadRequest("not a declared response type");
}
