#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.EventArguments
{

    public class BeforeBodyWriteEventArgs : ProxyEventArgsBase
    {
        internal BeforeBodyWriteEventArgs(SessionEventArgs session, byte[] bodyBytes, bool isChunked, bool isLastChunk) : base(session.Server, session.ClientConnection)
        {
            Session = session;
            BodyBytes = bodyBytes;
            IsChunked = isChunked;
            IsLastChunk = isLastChunk;
        }


        /// <value>
        ///     The session arguments.
        /// </value>
        public SessionEventArgs Session { get; }

        /// <summary>
        ///  Indicates whether body is written chunked stream.
        ///  If this is true, BeforeRequestBodySend or BeforeResponseBodySend will be called until IsLastChunk is false.
        /// </summary>
        public bool IsChunked { get; }

        /// <summary>
        /// Indicates if this is the last chunk from client or server stream, when request is chunked.
        /// Override this property to true if there are more bytes to write.
        /// </summary>
        public bool IsLastChunk { get; set; }

        /// <summary>
        /// The bytes about to be written. If IsChunked is true, this will be a chunk of the bytes to be written.
        /// Override this property with custom bytes if needed, and adjust IsLastChunk accordingly.
        /// </summary>
        public byte[] BodyBytes { get; set; }
    }
}
#endif
