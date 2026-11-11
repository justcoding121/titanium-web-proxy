using Microsoft.VisualStudio.TestTools.UnitTesting;

// Inspector VM body-save and disk-cache tests share temp paths / file handles; class-level
// workers > 1 races File.Open. Cross-project CI parallelism remains.
[assembly: Parallelize(Workers = 1, Scope = ExecutionScope.ClassLevel)]
