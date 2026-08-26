using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Http;

/// <summary>
///     The http header collection.
/// </summary>
[TypeConverter(typeof(ExpandableObjectConverter))]
public class HeaderCollection : IEnumerable<HttpHeader>
{
    private readonly Dictionary<string, HttpHeader> headers;

    private readonly Dictionary<string, List<HttpHeader>> nonUniqueHeaders;

    private readonly Dictionary<string, IReadOnlyList<HttpHeader>> nonUniqueHeadersReadOnly;

    /// <summary>
    ///     Monotonic counter bumped on every mutating API (<see cref="AddHeader"/>, <see cref="RemoveHeader"/>,
    ///     <see cref="Clear"/>, <see cref="SetOrAddHeaderValue"/>). Used by H2/H3/H1 intercept fast-finish to
    ///     detect whether session handlers changed headers after the wire decode / lite seed.
    /// </summary>
    internal int MutationCount { get; private set; }

    /// <summary>
    ///     Initializes a new instance of the <see cref="HeaderCollection" /> class.
    /// </summary>
    public HeaderCollection()
    {
        headers = new Dictionary<string, HttpHeader>(StringComparer.OrdinalIgnoreCase);
        nonUniqueHeaders = new Dictionary<string, List<HttpHeader>>(StringComparer.OrdinalIgnoreCase);
        nonUniqueHeadersReadOnly =
            new Dictionary<string, IReadOnlyList<HttpHeader>>(StringComparer.OrdinalIgnoreCase);
        Headers = new ReadOnlyDictionary<string, HttpHeader>(headers);
        NonUniqueHeaders = new ReadOnlyDictionary<string, IReadOnlyList<HttpHeader>>(nonUniqueHeadersReadOnly);
    }

    /// <summary>
    ///     Unique Request header collection.
    /// </summary>
    public ReadOnlyDictionary<string, HttpHeader> Headers { get; }

    /// <summary>
    ///     Non-unique headers. Values are read-only views over the internal lists so callers cannot
    ///     <c>Add</c>/<c>Clear</c> storage that still belongs to this collection.
    /// </summary>
    public ReadOnlyDictionary<string, IReadOnlyList<HttpHeader>> NonUniqueHeaders { get; }

    /// <summary>
    ///     Returns an enumerator that iterates through the collection.
    /// </summary>
    /// <returns>
    ///     An enumerator that can be used to iterate through the collection.
    /// </returns>
    /// <remarks>
    ///     Returns the concrete <see cref="Enumerator" /> struct rather than an interface type, matching
    ///     the pattern used by <see cref="List{T}" />/<see cref="Dictionary{TKey, TValue}" /> in the BCL:
    ///     a <c>foreach</c> over a variable statically typed as <see cref="HeaderCollection" /> binds to
    ///     this overload directly and allocates nothing, whereas the previous implementation (chaining
    ///     <see cref="Enumerable.Concat{TSource}" /> and <see cref="Enumerable.SelectMany{TSource, TResult}" />
    ///     over the two backing dictionaries) allocated two LINQ iterator objects on every call. This
    ///     matters here specifically because every outgoing request and response header block is
    ///     enumerated at least once to serialize it onto the wire, making this one of the hottest paths
    ///     in the proxy. Code that holds this collection through the <see cref="IEnumerable{T}" />
    ///     interface (e.g. LINQ operators) still gets a correct enumerator via the explicit interface
    ///     implementation below, at the cost of one boxing allocation - unavoidable through that surface,
    ///     and unchanged from before this optimization.
    /// </remarks>
    public Enumerator GetEnumerator()
    {
        return new Enumerator(this);
    }

    IEnumerator<HttpHeader> IEnumerable<HttpHeader>.GetEnumerator()
    {
        return GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    /// <summary>
    ///     Walks first the unique-header dictionary's values, then each list in the non-unique-header
    ///     dictionary's values in turn - the same effective order as the previous
    ///     <c>Concat(...SelectMany(...))</c> implementation - without allocating any LINQ iterator
    ///     objects. See the remarks on <see cref="GetEnumerator" /> for why this is worth a hand-written
    ///     enumerator instead of the equivalent LINQ expression.
    /// </summary>
    public struct Enumerator : IEnumerator<HttpHeader>
    {
        private Dictionary<string, HttpHeader>.ValueCollection.Enumerator uniqueEnumerator;
        private Dictionary<string, List<HttpHeader>>.ValueCollection.Enumerator nonUniqueOuterEnumerator;
        private List<HttpHeader>.Enumerator nonUniqueInnerEnumerator;
        private bool doneWithUnique;
        private bool hasInnerEnumerator;

        internal Enumerator(HeaderCollection collection)
        {
            uniqueEnumerator = collection.headers.Values.GetEnumerator();
            nonUniqueOuterEnumerator = collection.nonUniqueHeaders.Values.GetEnumerator();
            nonUniqueInnerEnumerator = default;
            doneWithUnique = false;
            hasInnerEnumerator = false;
            Current = null!;
        }

        public HttpHeader Current { get; private set; }

        object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            if (!doneWithUnique)
            {
                if (uniqueEnumerator.MoveNext())
                {
                    Current = uniqueEnumerator.Current;
                    return true;
                }

                doneWithUnique = true;
            }

            while (true)
            {
                if (hasInnerEnumerator)
                {
                    if (nonUniqueInnerEnumerator.MoveNext())
                    {
                        Current = nonUniqueInnerEnumerator.Current;
                        return true;
                    }

                    hasInnerEnumerator = false;
                }

                if (!nonUniqueOuterEnumerator.MoveNext())
                {
                    Current = null!;
                    return false;
                }

                nonUniqueInnerEnumerator = nonUniqueOuterEnumerator.Current.GetEnumerator();
                hasInnerEnumerator = true;
            }
        }

        public void Reset()
        {
            throw new NotSupportedException();
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    ///     True if header exists
    /// </summary>
    /// <param name="name"></param>
    /// <returns></returns>
    public bool HeaderExists(string name)
    {
        return headers.ContainsKey(name) || nonUniqueHeaders.ContainsKey(name);
    }

    /// <summary>
    ///     Returns all headers with given name if exists
    ///     Returns null if doesn't exist
    /// </summary>
    /// <param name="name"></param>
    /// <returns></returns>
    public List<HttpHeader>? GetHeaders(string name)
    {
        if (headers.TryGetValue(name, out var header))
            return new List<HttpHeader>
            {
                header
            };

        if (nonUniqueHeaders.TryGetValue(name, out var nonUnique))
            return new List<HttpHeader>(nonUnique);

        return null;
    }

    public HttpHeader? GetFirstHeader(string name)
    {
        if (headers.TryGetValue(name, out var header)) return header;

        if (nonUniqueHeaders.TryGetValue(name, out var h)) return h.FirstOrDefault();

        return null;
    }

    internal HttpHeader? GetFirstHeader(KnownHeader name)
    {
        if (headers.TryGetValue(name.String, out var header)) return header;

        if (nonUniqueHeaders.TryGetValue(name.String, out var h)) return h.FirstOrDefault();

        return null;
    }

    /// <summary>
    ///     True when exactly one header of this name exists (not the duplicate/non-unique bag).
    /// </summary>
    internal bool TryGetUniqueHeader(KnownHeader name, out HttpHeader header)
    {
        if (headers.TryGetValue(name.String, out header!))
            return true;

        header = null!;
        return false;
    }

    /// <summary>
    ///     Returns all headers
    /// </summary>
    /// <returns></returns>
    public List<HttpHeader> GetAllHeaders()
    {
        // Pre-sized and hand-iterated instead of `headers.Select(...)`/`nonUniqueHeaders.SelectMany(...)`:
        // this is called once per HTTP/3 request/response (see Http3RequestStream/Http3OriginBridge), so
        // the LINQ iterator allocations were a repeated per-message cost for no benefit over a plain loop.
        // headers.Count + nonUniqueHeaders.Count undercounts when any non-unique entry has more than one
        // value, but it is still a better starting capacity than the default (0), and List<T> grows from
        // there exactly as it would have without this hint.
        var result = new List<HttpHeader>(headers.Count + nonUniqueHeaders.Count);

        foreach (var header in headers.Values) result.Add(header);

        foreach (var list in nonUniqueHeaders.Values)
        {
            foreach (var header in list)
                result.Add(header);
        }

        return result;
    }

    /// <summary>
    ///     Add a new header with given name and value
    /// </summary>
    /// <param name="name"></param>
    /// <param name="value"></param>
    public void AddHeader(string name, string value)
    {
        AddHeader(new HttpHeader(name, value));
    }

    internal void AddHeader(KnownHeader name, string value)
    {
        AddHeader(new HttpHeader(name, value));
    }

    internal void AddHeader(KnownHeader name, KnownHeader value)
    {
        AddHeader(new HttpHeader(name, value));
    }

    /// <summary>
    ///     Adds the given header object to Request
    /// </summary>
    /// <param name="newHeader"></param>
    public void AddHeader(HttpHeader newHeader)
    {
        MutationCount++;
        // if header exist in non-unique header collection add it there
        if (nonUniqueHeaders.TryGetValue(newHeader.Name, out var list))
        {
            list.Add(newHeader);
            return;
        }

        // if header is already in unique header collection then move both to non-unique collection
        if (headers.TryGetValue(newHeader.Name, out var existing))
        {
            headers.Remove(newHeader.Name);

            var moved = new List<HttpHeader>
            {
                existing,
                newHeader
            };
            nonUniqueHeaders.Add(newHeader.Name, moved);
            nonUniqueHeadersReadOnly.Add(newHeader.Name, new ReadOnlyCollection<HttpHeader>(moved));
        }
        else
        {
            // add to unique header collection
            headers.Add(newHeader.Name, newHeader);
        }
    }

    /// <summary>
    ///     Adds the given header objects to Request
    /// </summary>
    /// <param name="newHeaders"></param>
    public void AddHeaders(IEnumerable<HttpHeader>? newHeaders)
    {
        if (newHeaders == null) return;

        foreach (var header in newHeaders) AddHeader(header);
    }

    /// <summary>
    ///     Adds the given header objects to Request
    /// </summary>
    /// <param name="newHeaders"></param>
    public void AddHeaders(IEnumerable<KeyValuePair<string, string>>? newHeaders)
    {
        if (newHeaders == null) return;

        foreach (var header in newHeaders) AddHeader(header.Key, header.Value);
    }

    /// <summary>
    ///     Adds the given header objects to Request
    /// </summary>
    /// <param name="newHeaders"></param>
    public void AddHeaders(IEnumerable<KeyValuePair<string, HttpHeader>>? newHeaders)
    {
        if (newHeaders == null) return;

        foreach (var header in newHeaders)
        {
            if (header.Key != header.Value.Name)
                throw new ArgumentException(
                    "Header name mismatch. Key and the name of the HttpHeader object should be the same.");

            AddHeader(header.Value);
        }
    }

    /// <summary>
    ///     removes all headers with given name
    /// </summary>
    /// <param name="headerName"></param>
    /// <returns>
    ///     True if header was removed
    ///     False if no header exists with given name
    /// </returns>
    public bool RemoveHeader(string headerName)
    {
        var result = headers.Remove(headerName);

        // do not convert to '||' expression to avoid lazy evaluation
        if (nonUniqueHeaders.Remove(headerName))
        {
            nonUniqueHeadersReadOnly.Remove(headerName);
            result = true;
        }

        if (result) MutationCount++;
        return result;
    }

    /// <summary>
    ///     removes all headers with given name
    /// </summary>
    /// <param name="headerName"></param>
    /// <returns>
    ///     True if header was removed
    ///     False if no header exists with given name
    /// </returns>
    public bool RemoveHeader(KnownHeader headerName)
    {
        var result = headers.Remove(headerName.String);

        // do not convert to '||' expression to avoid lazy evaluation
        if (nonUniqueHeaders.Remove(headerName.String))
        {
            nonUniqueHeadersReadOnly.Remove(headerName.String);
            result = true;
        }

        if (result) MutationCount++;
        return result;
    }

    /// <summary>
    ///     Removes given header object if it exist
    /// </summary>
    /// <param name="header">Returns true if header exists and was removed </param>
    public bool RemoveHeader(HttpHeader header)
    {
        if (headers.TryGetValue(header.Name, out var existing))
        {
            if (!existing.Equals(header)) return false;
            if (headers.Remove(header.Name))
            {
                MutationCount++;
                return true;
            }

            return false;
        }

        if (nonUniqueHeaders.TryGetValue(header.Name, out var matchingHeaders) &&
            matchingHeaders.RemoveAll(x => x.Equals(header)) > 0)
        {
            MutationCount++;
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Removes all the headers.
    /// </summary>
    public void Clear()
    {
        if (headers.Count > 0 || nonUniqueHeaders.Count > 0)
            MutationCount++;
        headers.Clear();
        nonUniqueHeaders.Clear();
        nonUniqueHeadersReadOnly.Clear();
    }

    /// <summary>
    ///     Rewrites Title-Case HTTP/1.1 field names to lowercase ASCII in place (RFC 9113 / 9114).
    ///     Used before HPACK/QPACK encode so the hot path can skip per-field <c>ToLowerInvariant</c>.
    ///     No-op when names are already lowercase (common for HTTP/2 origins and some H1 stacks).
    /// </summary>
    internal void NormalizeNamesToLowercaseAscii()
    {
        var needsRename = false;
        foreach (var header in this) // NOSONAR S3267 -- Explicit loop avoids LINQ enumerator allocation on hot path.
        {
            if (HeaderNameDataHasUpperCaseAscii(header.NameData))
            {
                needsRename = true;
                break;
            }
        }

        if (!needsRename)
            return;

        var renamed = new List<HttpHeader>(headers.Count + nonUniqueHeaders.Count);
        foreach (var header in this)
        {
            var nameData = header.NameData;
            if (HeaderNameDataHasUpperCaseAscii(nameData))
                nameData = AsciiToLowerByteString(nameData);
            renamed.Add(new HttpHeader(nameData, header.ValueData));
        }

        Clear();
        foreach (var header in renamed)
            AddHeader(header);
    }

    private static bool HeaderNameDataHasUpperCaseAscii(ByteString name)
    {
        var span = name.Span;
        for (var i = 0; i < span.Length; i++)
        {
            if (span[i] is >= (byte)'A' and <= (byte)'Z')
                return true;
        }

        return false;
    }

    private static ByteString AsciiToLowerByteString(ByteString name)
    {
        var span = name.Span;
        var buf = new byte[span.Length];
        for (var i = 0; i < span.Length; i++)
        {
            var c = span[i];
            buf[i] = c is >= (byte)'A' and <= (byte)'Z' ? (byte)(c + 32) : c;
        }

        return new ByteString(buf);
    }

    internal string? GetHeaderValueOrNull(KnownHeader headerName)
    {
        if (headers.TryGetValue(headerName.String, out var header)) return header.Value;

        return null;
    }

    internal void SetOrAddHeaderValue(KnownHeader headerName, string? value)
    {
        if (value == null)
        {
            RemoveHeader(headerName);
            return;
        }

        if (headers.TryGetValue(headerName.String, out var header))
            header.SetValue(value);
        else
            headers.Add(headerName.String, new HttpHeader(headerName, value));
    }

    internal void SetOrAddHeaderValue(KnownHeader headerName, KnownHeader value)
    {
        if (headers.TryGetValue(headerName.String, out var header))
            header.SetValue(value);
        else
            headers.Add(headerName.String, new HttpHeader(headerName, value));
    }

    /// <summary>
    ///     RFC 9112 §6.3: a message MUST NOT carry both Transfer-Encoding and Content-Length.
    ///     Recipients must either reject the message or remove Content-Length and honour TE.
    ///     Stripping Content-Length closes the request-smuggling ambiguity while remaining
    ///     interoperable with origins that incorrectly send both.
    ///     Also validates that "chunked" is the final Transfer-Encoding coding when present.
    /// </summary>
    internal void NormalizeMessageFraming()
    {
        // RFC 9112 §6.3: CL + TE conflict → strip CL (request-smuggling defence).
        if (HeaderExists(KnownHeaders.TransferEncoding.String) &&
            HeaderExists(KnownHeaders.ContentLength.String))
            RemoveHeader(KnownHeaders.ContentLength);

        // Validate Transfer-Encoding chain: chunked must be the final coding if present.
        var teHeader = GetHeaderValueOrNull(KnownHeaders.TransferEncoding.String);
        if (teHeader != null)
        {
            var codings = teHeader.Split(',')
                .Select(s => s.Trim().ToLowerInvariant())
                .Where(s => s.Length > 0)
                .ToList();

            if (codings.Count > 1)
            {
                // If "chunked" appears in a non-final position, normalize to just "chunked"
                // to remove the framing ambiguity.
                for (var i = 0; i < codings.Count - 1; i++)
                {
                    if (codings[i] == "chunked")
                    {
                        SetOrAddHeaderValue(KnownHeaders.TransferEncoding.String, "chunked");
                        break;
                    }
                }
            }
        }
    }

    /// <summary>
    ///     Fix proxy specific headers
    /// </summary>
    internal void FixProxyHeaders()
    {
        // If proxy-connection close was returned inform to close the connection
        var proxyHeader = GetHeaderValueOrNull(KnownHeaders.ProxyConnection);
        RemoveHeader(KnownHeaders.ProxyConnection);

        if (proxyHeader != null) SetOrAddHeaderValue(KnownHeaders.Connection, proxyHeader);

        NormalizeMessageFraming();
    }
}