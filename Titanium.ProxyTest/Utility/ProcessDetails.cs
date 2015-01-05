using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Management;
using System.Diagnostics;

namespace Titanium.ProxyManager.Utitlity
{
 
    public  class TcpHelperUtil
    {
        private const short MINIMUM_TOKEN_IN_A_LINE = 5;
        private const string COMMAND_EXE = @"cmd.exe";

        public static int GetProcessId(int port)
        {
            try
            {
 
                // execute netstat command for the given port
                string commandArgument = string.Format("/c netstat -an -o -p tcp|findstr \":{0}.*ESTABLISHED", port);

                string commandOut = ExecuteCommandAndCaptureOutput(COMMAND_EXE, commandArgument);
                if (string.IsNullOrEmpty(commandOut))
                {
                    // port is not in use
                    return 0;
                }

                string[] stringTokens = commandOut.Split(default(Char[]), StringSplitOptions.RemoveEmptyEntries);
                if (stringTokens.Length < MINIMUM_TOKEN_IN_A_LINE)
                {
                    return 0;
                }

             

                if (stringTokens.Length == 10)
                    return int.Parse(stringTokens[9].Trim());
                else
                    return 0;

            }
            catch { return 0; }

        }

        /// <summary>
        /// Execute the given command and captures the output
        /// </summary>
        /// <param name="commandName"></param>
        /// <param name="arguments"></param>
        /// <returns></returns>
        private static string ExecuteCommandAndCaptureOutput(string commandName, string arguments)
        {
            string commandOut = string.Empty;
            Process process = new Process();
            process.StartInfo.FileName = commandName;
            process.StartInfo.Arguments = arguments;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
         
            process.Start();

   
            commandOut = process.StandardOutput.ReadToEnd();
            string errors = process.StandardError.ReadToEnd();
            try
            {
                process.WaitForExit(TimeSpan.FromSeconds(2).Milliseconds);
            }
            catch 
            {


            }
            return commandOut;
        }
    }
}
