using System;
using System.Text;

namespace Titanium.Web.Proxy.Examples.Basic
{
    public class SampleClientState
    {
        private readonly StringBuilder pipelineInfo = new();
        private readonly object pipelineLock = new();

        public void AppendPipeline(string line)
        {
            lock (pipelineLock)
                pipelineInfo.AppendLine(line);
        }

        public string GetPipelineInfo()
        {
            lock (pipelineLock)
                return pipelineInfo.ToString();
        }

        /// <summary>
        ///     UTC timestamp when the request entered <c>BeforeRequest</c>, used for elapsed timing in traffic logs.
        /// </summary>
        public DateTime RequestStartedUtc { get; set; }
    }
}
