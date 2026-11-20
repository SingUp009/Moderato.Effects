using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("browser")]

namespace Moderato.Effects.WasmRunner;

internal static class Program
{
    // The browser runtime requires an entry point; all work goes through the [JSExport] members of Runner.
    private static void Main()
    {
    }
}
