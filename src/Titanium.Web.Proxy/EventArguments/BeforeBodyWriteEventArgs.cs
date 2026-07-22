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
        ///  Indicates whether the body is written as a chunked stream.
        ///  If this is true, OnRequestBodyWrite/OnResponseBodyWrite will be called
        ///  for each chunk until IsLastChunk becomes true.
        /// </summary>
        public bool IsChunked { get; }

        /// <summary>
        /// Indicates whether this is the last chunk from the client or server stream, when the body is chunked.
        /// This is true when the source stream has reached its end. Set this to true from a handler to stop
        /// writing further chunks to the target stream (the terminating chunk will be written).
        /// </summary>
        public bool IsLastChunk { get; set; }

        /// <summary>
        /// The bytes about to be written. If IsChunked is true, this will be a chunk of the bytes to be written.
        /// Override this property with custom bytes if needed, and adjust IsLastChunk accordingly.
        /// </summary>
        public byte[] BodyBytes { get; set; }
    }
}
