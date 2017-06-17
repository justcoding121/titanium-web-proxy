using System.Collections;
using System.Collections.Generic;

namespace Titanium.Web.Proxy.Network.Tcp
{
    /// <summary>
    /// Represents collection of TcpRows
    /// </summary>
    /// <seealso>
    ///     <cref>System.Collections.Generic.IEnumerable{Proxy.Tcp.TcpRow}</cref>
    /// </seealso>
    internal class TcpTable : IEnumerable<TcpRow>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="TcpTable"/> class.
        /// </summary>
        /// <param name="tcpRows">TcpRow collection to initialize with.</param>
        internal TcpTable(IEnumerable<TcpRow> tcpRows)
        {
            this.TcpRows = tcpRows;
        }

        /// <summary>
        /// Gets the TCP rows.
        /// </summary>
        internal IEnumerable<TcpRow> TcpRows { get; }

        /// <summary>
        /// Returns an enumerator that iterates through the collection.
        /// </summary>
        /// <returns>An enumerator that can be used to iterate through the collection.</returns>
        public IEnumerator<TcpRow> GetEnumerator()
        {
            return TcpRows.GetEnumerator();
        }

        /// <summary>
        /// Returns an enumerator that iterates through a collection.
        /// </summary>
        /// <returns>An <see cref="T:System.Collections.IEnumerator" /> object that can be used to iterate through the collection.</returns>
        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
