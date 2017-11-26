using System;
using System.Runtime.InteropServices;

namespace Titanium.Web.Proxy.Helpers.WinHttp
{
    internal class WinHttpHandle : SafeHandle
    {
        public WinHttpHandle() : base(IntPtr.Zero, true)
        {
        }

        protected override bool ReleaseHandle()
        {
            return NativeMethods.WinHttp.WinHttpCloseHandle(handle);
        }

        public override bool IsInvalid => handle == IntPtr.Zero;
    }
}
