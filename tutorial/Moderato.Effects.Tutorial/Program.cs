using Moderato.Effects.Tutorial;

switch (args)
{
    case ["01"]:
        ResultsChapter.Run();
        return 0;
    case ["02"]:
        await FirstEffectChapter.RunAsync();
        return 0;
    case ["03"]:
        await ExpectedErrorsChapter.RunAsync();
        return 0;
    case ["04"]:
        await ComposingEffectsChapter.RunAsync();
        return 0;
    case ["05"]:
        await ManagingDependenciesChapter.RunAsync();
        return 0;
    case ["06"]:
        await ManagingResourcesChapter.RunAsync();
        return 0;
    case ["07"]:
        await ConcurrentEffectsChapter.RunAsync();
        return 0;
    case [] or ["--help"]:
        Console.WriteLine("""
            Usage: dotnet run --project tutorial/Moderato.Effects.Tutorial -- <chapter>
            01 - Success and Failure with Result
            02 - Your First Effect
            03 - Handling Expected Errors
            04 - Composing Effects
            05 - Managing Dependencies
            06 - Managing Resources
            07 - Running Effects Concurrently
            """);
        return 0;
    default:
        Console.Error.WriteLine("Choose a chapter from 01 to 07. Use --help to list the chapters.");
        return 1;
}
