using Moderato.Effects.Tutorial;

switch (args)
{
    case ["01"]:
        ResultsChapter.Run();
        return 0;
    case ["02"]:
        await FirstEffectChapter.RunAsync();
        return 0;
    case [] or ["--help"]:
        Console.WriteLine("""
            Usage: dotnet run --project tutorial/Moderato.Effects.Tutorial -- <chapter>
            01 - Success and Failure with Result
            02 - Your First Effect
            """);
        return 0;
    default:
        Console.Error.WriteLine("Choose chapter 01 or 02. Use --help to list the chapters.");
        return 1;
}
