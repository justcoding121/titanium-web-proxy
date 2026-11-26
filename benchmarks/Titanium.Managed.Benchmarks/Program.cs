using System;
using BenchmarkDotNet.Running;

namespace Titanium.Managed.Benchmarks;

public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Pass benchmark arguments to run. With no arguments this process exits.");
            return;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
