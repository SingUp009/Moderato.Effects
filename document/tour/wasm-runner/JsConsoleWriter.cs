using System.Runtime.InteropServices.JavaScript;
using System.Text;

namespace Moderato.Effects.WasmRunner;

/// <summary>Forwards console output to JavaScript line by line, so it appears while the program is still running.</summary>
internal sealed partial class JsConsoleWriter(bool isError) : TextWriter
{
    private readonly StringBuilder buffer = new();

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        buffer.Append(value);
        if (value == '\n')
        {
            Flush();
        }
    }

    public override void Write(string? value)
    {
        buffer.Append(value);
        if (value is not null && value.Contains('\n', StringComparison.Ordinal))
        {
            Flush();
        }
    }

    public override void Flush()
    {
        if (buffer.Length == 0)
        {
            return;
        }

        PostOutput(buffer.ToString(), isError);
        buffer.Clear();
    }

    [JSImport("postOutput", "runner")]
    private static partial void PostOutput(string text, bool isError);
}
