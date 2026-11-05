using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

internal static partial class SignalVerification
{
    internal static async Task<int> Run(string[] command)
    {
        if (command.Length == 0)
        {
            Console.Error.WriteLine("Usage: SoakProbe verify-signals <probe executable command ...>");
            return 2;
        }
        if (OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Signal verification requires a POSIX platform.");
            return 2;
        }
        try
        {
            foreach (var scenario in new[] { "gen", "resource", "provide", "bind", "cancel", "fiber", "die", "host" })
            {
                await Verify(command, 2, "SIGINT", scenario);
                await Verify(command, 15, "SIGTERM", scenario);
            }
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task Verify(string[] command, int signal, string signalName, string scenario)
    {
        // Given
        var output = new StringBuilder();
        var gate = new object();
        var start = new ProcessStartInfo(command[0])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in command.Skip(1).Concat(new[] { scenario, "5", "0.1" })) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        DataReceivedEventHandler capture = (_, message) =>
        {
            if (message.Data is not null)
                lock (gate) output.AppendLine(message.Data);
        };
        process.OutputDataReceived += capture;
        process.ErrorDataReceived += capture;
        process.Start();
        string Snapshot()
        {
            lock (gate) return output.ToString();
        }
        try
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var ready = Stopwatch.StartNew();
            while (!ObservationRow().IsMatch(Snapshot()))
            {
                if (process.HasExited || ready.Elapsed >= TimeSpan.FromSeconds(15))
                    throw new InvalidOperationException($"{scenario} did not reach its first observation window:\n{Snapshot()}");
                await Task.Delay(50);
            }

            // When
            if (SendSignal(process.Id, signal) != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Could not send {signalName} to {scenario}.");
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch (TimeoutException exception)
            {
                throw new InvalidOperationException($"{scenario}: {signalName} did not stop the probe.", exception);
            }

            // Then
            ValidateOutput(scenario, process.ExitCode, Snapshot());
            Console.WriteLine($"PASS {scenario} {signalName}: stopped after cleanup and final checks; exit=130");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static void ValidateOutput(string scenario, int exitCode, string text)
    {
        if (exitCode != 130)
            throw new InvalidOperationException($"Expected interrupted-probe exit 130, got {exitCode}:\n{text}");
        if (scenario == "host")
        {
            if (!text.Contains("STOPPED Host lifetime request; timed run incomplete", StringComparison.Ordinal) ||
                Regex.Matches(text, "PASS host-a ").Count != 1 || Regex.Matches(text, "PASS host-b ").Count != 1 ||
                Regex.Matches(text, "invalid=0 diagnosticDisposals=1").Count != 2)
            {
                throw new InvalidOperationException($"Stopped Host checks failed:\n{text}");
            }
        }
        else
        {
            var stopped = StoppedRow().Matches(text);
            if (stopped.Count != 1 || text.Contains(",Infinity,", StringComparison.Ordinal) || text.Contains(",NaN,", StringComparison.Ordinal) ||
                Regex.IsMatch(text, "^PASS actualSeconds=", RegexOptions.Multiline))
            {
                throw new InvalidOperationException($"Stopped {scenario} checks failed:\n{text}");
            }
            var operations = long.Parse(stopped[0].Groups[1].Value, CultureInfo.InvariantCulture);
            var acquired = long.Parse(stopped[0].Groups[2].Value, CultureInfo.InvariantCulture);
            var released = long.Parse(stopped[0].Groups[3].Value, CultureInfo.InvariantCulture);
            if (operations <= 0 || operations != acquired || acquired != released)
                throw new InvalidOperationException($"Stopped {scenario} did not finish its acquisitions and releases:\n{text}");
        }
    }

    internal static void CheckOutputControls()
    {
        // Given
        const string STOPPED = "STOPPED actualSeconds=0.1 operations=1 acquired=1 released=1 errors=0\n";
        const string HOST = "PASS host-a invalid=0 diagnosticDisposals=1\nPASS host-b invalid=0 diagnosticDisposals=1\nSTOPPED Host lifetime request; timed run incomplete\n";
        // When
        ValidateOutput("gen", 130, STOPPED);
        ValidateOutput("host", 130, HOST);
        // Then
        Reject("gen", 0, STOPPED);
        Reject("gen", 130, "");
        Reject("gen", 130, STOPPED + STOPPED);
        Reject("gen", 130, STOPPED.Replace("=1", "=0", StringComparison.Ordinal));
        Reject("gen", 130, STOPPED.Replace("released=1", "released=0", StringComparison.Ordinal));
        Reject("gen", 130, STOPPED + "0,Infinity,0\n");
        Reject("gen", 130, STOPPED + "0,NaN,0\n");
        Reject("gen", 130, STOPPED + "PASS actualSeconds=0.1\n");
        Reject("host", 130, HOST.Replace("STOPPED Host lifetime request; timed run incomplete", "", StringComparison.Ordinal));
        Reject("host", 130, HOST.Replace("host-a", "host-b", StringComparison.Ordinal));
        Reject("host", 130, HOST.Replace("diagnosticDisposals=1", "diagnosticDisposals=2", StringComparison.Ordinal));
        Reject("host", 130, HOST.Replace("invalid=0", "invalid=1", StringComparison.Ordinal));
        Console.WriteLine("PASS signal output controls: invalid exits, counts and stop markers are rejected");

        static void Reject(string scenario, int exitCode, string text)
        {
            try { ValidateOutput(scenario, exitCode, text); }
            catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("Invalid signal output was accepted.");
        }
    }

    [GeneratedRegex(@"^\d+\.\d+,\d+,", RegexOptions.Multiline, 1000)]
    private static partial Regex ObservationRow();

    [GeneratedRegex(@"^STOPPED actualSeconds=[\d.]+ operations=(\d+) acquired=(\d+) released=(\d+) errors=(\d+)$", RegexOptions.Multiline, 1000)]
    private static partial Regex StoppedRow();

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int SendSignal(int processId, int signal);
}
