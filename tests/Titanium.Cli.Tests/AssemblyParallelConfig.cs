using Microsoft.VisualStudio.TestTools.UnitTesting;

// Cli tests share process-wide AppDomain assembly load state (e.g. leftover MaxMind.Db
// assertions) and metrics listener ports — keep class execution serial within the assembly.
// Cross-project parallelism in CI still overlaps Cli with other assemblies safely.
[assembly: Parallelize(Workers = 1, Scope = ExecutionScope.ClassLevel)]
