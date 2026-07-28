using System;
using System.Text;

namespace Titanium.Web.Proxy.Examples.Basic
{
    public class SampleClientState
    {
        public StringBuilder PipelineInfo { get; } = new StringBuilder();

        /// <summary>
        ///     UTC timestamp when the request entered <c>BeforeRequest</c>, used for elapsed timing in traffic logs.
        /// </summary>
        public DateTime RequestStartedUtc { get; set; }
    }
}
