using BenchmarkDotNet.Running;

// dotnet run -c Release --project bench/Moderato.Effects.Benchmarks -- --filter * --inProcess
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
