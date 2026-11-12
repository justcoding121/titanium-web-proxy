using Microsoft.VisualStudio.TestTools.UnitTesting;

// UnitTests includes shared on-disk cert cache paths and static ProxyLog sinks; keep classes
// serial within this assembly. CI still overlaps this project with other unit assemblies.
[assembly: Parallelize(Workers = 1, Scope = ExecutionScope.ClassLevel)]
