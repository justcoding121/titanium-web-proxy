using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Titanium.Web.Proxy.Exceptions
{
    public class BodyNotFoundException : Exception
    {
        public BodyNotFoundException(string message)
            :base(message)
        {

        }
     
    }
}
