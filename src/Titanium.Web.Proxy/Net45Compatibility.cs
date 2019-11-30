#if NET45
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy
{
    class Net45Compatibility
    {
        public static byte[] EmptyArray = new byte[0];

        public static Task CompletedTask = new Task(() => { });
    }
}
#endif
