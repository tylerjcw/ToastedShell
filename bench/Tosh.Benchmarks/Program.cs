using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Running;
using Tosh.Benchmarks;

// Entry point — runs all benchmarks in this assembly.
//   dotnet run -c Release --project bench/Tosh.Benchmarks
//   dotnet run -c Release --project bench/Tosh.Benchmarks -- --filter '*Binder*'
var config = DefaultConfig.Instance.AddDiagnoser(MemoryDiagnoser.Default);
BenchmarkSwitcher.FromAssembly(typeof(BinderBenchmarks).Assembly).Run(args, config);
