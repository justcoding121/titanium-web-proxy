using System;
using BenchmarkDotNet.Running;

namespace Titanium.Managed.Benchmarks;

public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Pass BenchmarkDotNet arguments, or 'probe' with alloc, rsa, idle, storm, machine, or quic.");
            return;
        }

        if (string.Equals(args[0], "probe", StringComparison.Ordinal))
        {
            var rest = new string[args.Length - 1];
            Array.Copy(args, 1, rest, 0, rest.Length);
            ProbeCommand.Run(rest);
            return;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
