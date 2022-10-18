using System.ServiceProcess;

namespace WindowsServiceExample
{
    internal static class Program
    {
        /// <summary>
        ///     The main entry point for the application.
        /// </summary>
        private static void Main()
        {
            ServiceBase[] servicesToRun;
            servicesToRun = new ServiceBase[]
            {
                new ProxyService()
            };
            ServiceBase.Run(servicesToRun);
        }
    }
}