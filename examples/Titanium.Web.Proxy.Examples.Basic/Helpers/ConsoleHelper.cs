using System;
using System.Runtime.InteropServices;

namespace Titanium.Web.Proxy.Examples.Basic.Helpers
{
    /// <summary>
    ///     Adapted from
    ///     http://stackoverflow.com/questions/13656846/how-to-programmatic-disable-c-sharp-console-applications-quick-edit-mode
    /// </summary>
    internal static class ConsoleHelper
    {
        private const uint EnableQuickEdit = 0x0040;

        // STD_INPUT_HANDLE (DWORD): -10 is the standard input device.
        private const int StdInputHandle = -10;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll")]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll")]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

        internal static bool DisableQuickEditMode()
        {
            var consoleHandle = GetStdHandle(StdInputHandle);

            // get current console mode
            if (!GetConsoleMode(consoleHandle, out var consoleMode))
                // ERROR: Unable to get console mode.
                return false;

            // Clear the quick edit bit in the mode flags
            consoleMode &= ~EnableQuickEdit;

            // set the new mode
            if (!SetConsoleMode(consoleHandle, consoleMode))
                // ERROR: Unable to set console mode
                return false;

            return true;
        }
    }
}