using Moderato.Results;

foreach (var quantity in new[] { 2, 0, -1 })
{
    Result<int, string> result = ValidateQuantity(quantity);

    Console.WriteLine(result switch
    {
        Ok<int>(var accepted) => $"Accepted quantity: {accepted}",
        Err<string>(var error) => $"Invalid quantity: {error}",
    });
}

static Result<int, string> ValidateQuantity(int quantity)
{
    if (quantity <= 0)
        return new Err<string>("Quantity must be greater than zero.");

    return new Ok<int>(quantity);
}
