Console.WriteLine(IsEven(2));
Console.WriteLine(LoggedIsEven(2));

var expiresAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
Console.WriteLine(HasExpired(expiresAt, now: expiresAt.AddSeconds(-1)));
Console.WriteLine(HasExpired(expiresAt, now: expiresAt));

static bool IsEven(int number) => number % 2 == 0;

static bool LoggedIsEven(int number)
{
    Console.WriteLine($"Checking {number}");
    return number % 2 == 0;
}

static bool HasExpired(DateTimeOffset expiresAt, DateTimeOffset now) =>
    now >= expiresAt;
