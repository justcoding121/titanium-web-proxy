using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.EventArguments
{
    internal enum TransformationMode
    {
        None,

        /// <summary>
        /// Removes the chunked encoding
        /// </summary>
        RemoveChunked,

        /// <summary>
        /// Uncompress the body (this also removes the chunked encoding if exists)
        /// </summary>
        Uncompress,
    }
}
