using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Http
{
    public class HeaderCollection : IEnumerable<HttpHeader>
    {
        /// <summary>
        /// Unique Request header collection
        /// </summary>
        public Dictionary<string, HttpHeader> Headers { get; set; }

        /// <summary>
        /// Non Unique headers
        /// </summary>
        public Dictionary<string, List<HttpHeader>> NonUniqueHeaders { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="HeaderCollection"/> class.
        /// </summary>
        public HeaderCollection()
        {
            Headers = new Dictionary<string, HttpHeader>(StringComparer.OrdinalIgnoreCase);
            NonUniqueHeaders = new Dictionary<string, List<HttpHeader>>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True if header exists
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public bool HeaderExists(string name)
        {
            return Headers.ContainsKey(name) || NonUniqueHeaders.ContainsKey(name);
        }

        /// <summary>
        /// Returns all headers with given name if exists
        /// Returns null if does'nt exist
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public List<HttpHeader> GetHeaders(string name)
        {
            if (Headers.ContainsKey(name))
            {
                return new List<HttpHeader>
                {
                    Headers[name]
                };
            }
            if (NonUniqueHeaders.ContainsKey(name))
            {
                return new List<HttpHeader>(NonUniqueHeaders[name]);
            }

            return null;
        }

        /// <summary>
        /// Returns all headers 
        /// </summary>
        /// <returns></returns>
        public List<HttpHeader> GetAllHeaders()
        {
            var result = new List<HttpHeader>();

            result.AddRange(Headers.Select(x => x.Value));
            result.AddRange(NonUniqueHeaders.SelectMany(x => x.Value));

            return result;
        }

        /// <summary>
        /// Add a new header with given name and value
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void AddHeader(string name, string value)
        {
            AddHeader(new HttpHeader(name, value));
        }

        /// <summary>
        /// Adds the given header object to Request
        /// </summary>
        /// <param name="newHeader"></param>
        public void AddHeader(HttpHeader newHeader)
        {
            if (NonUniqueHeaders.ContainsKey(newHeader.Name))
            {
                NonUniqueHeaders[newHeader.Name].Add(newHeader);
                return;
            }

            if (Headers.ContainsKey(newHeader.Name))
            {
                var existing = Headers[newHeader.Name];
                Headers.Remove(newHeader.Name);

                NonUniqueHeaders.Add(newHeader.Name, new List<HttpHeader>
                {
                    existing,
                    newHeader
                });
            }
            else
            {
                Headers.Add(newHeader.Name, newHeader);
            }
        }

        /// <summary>
        /// Adds the given header objects to Request
        /// </summary>
        /// <param name="newHeaders"></param>
        public void AddHeaders(IEnumerable<HttpHeader> newHeaders)
        {
            if (newHeaders == null)
            {
                return;
            }

            foreach (var header in newHeaders)
            {
                AddHeader(header);
            }
        }

        /// <summary>
        /// Adds the given header objects to Request
        /// </summary>
        /// <param name="newHeaders"></param>
        public void AddHeaders(IEnumerable<KeyValuePair<string, string>> newHeaders)
        {
            if (newHeaders == null)
            {
                return;
            }

            foreach (var header in newHeaders)
            {
                AddHeader(header.Key, header.Value);
            }
        }

        /// <summary>
        /// Adds the given header objects to Request
        /// </summary>
        /// <param name="newHeaders"></param>
        public void AddHeaders(IEnumerable<KeyValuePair<string, HttpHeader>> newHeaders)
        {
            if (newHeaders == null)
            {
                return;
            }

            foreach (var header in newHeaders)
            {
                if (header.Key != header.Value.Name)
                {
                    throw new Exception("Header name mismatch. Key and the name of the HttpHeader object should be the same.");
                }

                AddHeader(header.Value);
            }
        }

        /// <summary>
        ///  removes all headers with given name
        /// </summary>
        /// <param name="headerName"></param>
        /// <returns>True if header was removed
        /// False if no header exists with given name</returns>
        public bool RemoveHeader(string headerName)
        {
            bool result = Headers.Remove(headerName);

            // do not convert to '||' expression to avoid lazy evaluation
            if (NonUniqueHeaders.Remove(headerName))
            {
                result = true;
            }

            return result;
        }

        /// <summary>
        /// Removes given header object if it exist
        /// </summary>
        /// <param name="header">Returns true if header exists and was removed </param>
        public bool RemoveHeader(HttpHeader header)
        {
            if (Headers.ContainsKey(header.Name))
            {
                if (Headers[header.Name].Equals(header))
                {
                    Headers.Remove(header.Name);
                    return true;
                }
            }
            else if (NonUniqueHeaders.ContainsKey(header.Name))
            {
                if (NonUniqueHeaders[header.Name].RemoveAll(x => x.Equals(header)) > 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Removes all the headers.
        /// </summary>
        public void Clear()
        {
            Headers.Clear();
            NonUniqueHeaders.Clear();
        }

        internal string GetHeaderValueOrNull(string headerName)
        {
            HttpHeader header;
            if (Headers.TryGetValue(headerName, out header))
            {
                return header.Value;
            }

            return null;
        }

        internal string SetOrAddHeaderValue(string headerName, string value)
        {
            HttpHeader header;
            if (Headers.TryGetValue(headerName, out header))
            {
                header.Value = value;
            }
            else
            {
                Headers.Add(headerName, new HttpHeader(headerName, value));
            }

            return null;
        }

        /// <summary>
        /// Fix proxy specific headers
        /// </summary>
        internal void FixProxyHeaders()
        {
            //If proxy-connection close was returned inform to close the connection
            string proxyHeader = GetHeaderValueOrNull("proxy-connection");
            RemoveHeader("proxy-connection");

            if (proxyHeader != null)
            {
                SetOrAddHeaderValue("connection", proxyHeader);
            }
        }

        /// <summary>
        /// Returns an enumerator that iterates through the collection.
        /// </summary>
        /// <returns>
        /// An enumerator that can be used to iterate through the collection.
        /// </returns>
        public IEnumerator<HttpHeader> GetEnumerator()
        {
            return Headers.Values.Concat(NonUniqueHeaders.Values.SelectMany(x => x)).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
