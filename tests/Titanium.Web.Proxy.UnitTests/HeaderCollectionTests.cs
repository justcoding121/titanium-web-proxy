using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests
{
    /// <summary>
    ///     Unit tests for <see cref="HeaderCollection" />.
    ///     HeaderCollection underpins the <c>TrailingHeaders</c> property, so its core
    ///     unique/non-unique/add/remove/set semantics need a documented baseline.
    /// </summary>
    [TestClass]
    public class HeaderCollectionTests
    {
        private static readonly string[] expected = new[] { "1", "2" };

        [TestMethod]
        public void AddHeader_SameNameTwice_MovesBothValuesToNonUniqueCollection()
        {
            var headers = new HeaderCollection();
            headers.AddHeader("X-Test", "1");
            headers.AddHeader("X-Test", "2");

            Assert.IsFalse(headers.Headers.ContainsKey("X-Test"));
            Assert.IsTrue(headers.NonUniqueHeaders.ContainsKey("X-Test"));

            var all = headers.GetHeaders("X-Test");
            Assert.IsNotNull(all);
            Assert.AreEqual(2, all.Count);
            CollectionAssert.AreEquivalent(expected, all.ConvertAll(h => h.Value));
        }

        [TestMethod]
        public void AddHeader_SingleName_StaysInUniqueCollection()
        {
            var headers = new HeaderCollection();
            headers.AddHeader("X-Test", "1");

            Assert.IsTrue(headers.Headers.ContainsKey("X-Test"));
            Assert.IsFalse(headers.NonUniqueHeaders.ContainsKey("X-Test"));
        }

        [TestMethod]
        public void GetFirstHeader_ForNonUniqueHeaders_ReturnsFirstAddedValue()
        {
            var headers = new HeaderCollection();
            headers.AddHeader("Set-Cookie", "a=1");
            headers.AddHeader("Set-Cookie", "b=2");

            var first = headers.GetFirstHeader("Set-Cookie");

            Assert.IsNotNull(first);
            Assert.AreEqual("a=1", first.Value);
        }

        [TestMethod]
        public void GetHeaders_ForMissingName_ReturnsNull()
        {
            var headers = new HeaderCollection();

            Assert.IsNull(headers.GetHeaders("X-Missing"));
            Assert.IsNull(headers.GetFirstHeader("X-Missing"));
        }

        [TestMethod]
        public void RemoveHeader_ByName_RemovesFromEitherCollectionAndReportsWhetherItExisted()
        {
            var headers = new HeaderCollection();
            headers.AddHeader("X-Single", "1");
            headers.AddHeader("X-Multi", "a");
            headers.AddHeader("X-Multi", "b");

            Assert.IsTrue(headers.RemoveHeader("X-Single"));
            Assert.IsTrue(headers.RemoveHeader("X-Multi"));
            Assert.IsFalse(headers.HeaderExists("X-Single"));
            Assert.IsFalse(headers.HeaderExists("X-Multi"));
            Assert.IsFalse(headers.RemoveHeader("X-DoesNotExist"));
        }

        [TestMethod]
        public void HeaderExists_And_Lookups_AreCaseInsensitive()
        {
            var headers = new HeaderCollection();
            headers.AddHeader("Content-Type", "text/plain");

            Assert.IsTrue(headers.HeaderExists("content-type"));
            Assert.IsTrue(headers.HeaderExists("CONTENT-TYPE"));
            Assert.IsNotNull(headers.GetFirstHeader("content-type"));
        }

        [TestMethod]
        public void Clear_RemovesAllUniqueAndNonUniqueHeaders()
        {
            var headers = new HeaderCollection();
            headers.AddHeader("A", "1");
            headers.AddHeader("B", "1");
            headers.AddHeader("B", "2");

            headers.Clear();

            Assert.AreEqual(0, headers.GetAllHeaders().Count);
            Assert.IsFalse(headers.HeaderExists("A"));
            Assert.IsFalse(headers.HeaderExists("B"));
        }

        [TestMethod]
        public void SetOrAddHeaderValue_UpdatesExistingUniqueHeaderInPlaceRatherThanDuplicating()
        {
            var headers = new HeaderCollection();
            headers.AddHeader(KnownHeaders.ContentType, "text/plain");

            headers.SetOrAddHeaderValue(KnownHeaders.ContentType, "application/json");

            Assert.AreEqual("application/json", headers.GetHeaderValueOrNull(KnownHeaders.ContentType));
            Assert.AreEqual(1, headers.GetHeaders("Content-Type")!.Count);
        }

        [TestMethod]
        public void SetOrAddHeaderValue_NullValue_RemovesHeader()
        {
            var headers = new HeaderCollection();
            headers.AddHeader(KnownHeaders.ContentType, "text/plain");

            headers.SetOrAddHeaderValue(KnownHeaders.ContentType, null);

            Assert.IsFalse(headers.HeaderExists("Content-Type"));
        }

        [TestMethod]
        public void FixProxyHeaders_MovesProxyConnectionValueOntoConnectionHeader()
        {
            var headers = new HeaderCollection();
            headers.AddHeader(KnownHeaders.ProxyConnection, "close");

            headers.FixProxyHeaders();

            Assert.IsFalse(headers.HeaderExists("Proxy-Connection"));
            Assert.AreEqual("close", headers.GetHeaderValueOrNull(KnownHeaders.Connection));
        }

        [TestMethod]
        public void FixProxyHeaders_WithoutProxyConnectionHeader_LeavesConnectionHeaderUntouched()
        {
            var headers = new HeaderCollection();
            headers.AddHeader(KnownHeaders.Connection, "keep-alive");

            headers.FixProxyHeaders();

            Assert.AreEqual("keep-alive", headers.GetHeaderValueOrNull(KnownHeaders.Connection));
        }

        private static readonly string[] expectedUniqueHeaderNames = new[] { "X-Unique-1", "X-Unique-2" };

        /// <summary>
        ///     Phase F.18 allocation reduction: <see cref="HeaderCollection.GetEnumerator" /> was
        ///     rewritten from a LINQ <c>Concat(...SelectMany(...))</c> expression to a hand-written
        ///     struct enumerator (the every-message header-serialization path). These tests pin down
        ///     that the visible enumeration order and contents are unchanged: unique headers first (in
        ///     dictionary iteration order), then every non-unique header's values in insertion order,
        ///     grouped by name.
        /// </summary>
        [TestMethod]
        public void Foreach_YieldsUniqueHeadersThenNonUniqueHeadersInInsertionOrder()
        {
            var headers = new HeaderCollection();
            headers.AddHeader("X-Unique-1", "u1");
            headers.AddHeader("X-Multi", "m1");
            headers.AddHeader("X-Unique-2", "u2");
            headers.AddHeader("X-Multi", "m2");

            var seen = new List<(string Name, string Value)>();
            foreach (var header in headers) seen.Add((header.Name, header.Value));

            Assert.AreEqual(4, seen.Count);
            CollectionAssert.AreEquivalent(
                expectedUniqueHeaderNames,
                seen.Take(2).Select(h => h.Name).ToArray());
            CollectionAssert.AreEqual(
                new[] { ("X-Multi", "m1"), ("X-Multi", "m2") },
                seen.Skip(2).ToArray());
        }

        [TestMethod]
        public void Foreach_EmptyCollection_YieldsNothing()
        {
            var headers = new HeaderCollection();

            var count = 0;
            foreach (var _ in headers) count++;

            Assert.AreEqual(0, count);
        }

        private static readonly string[] expectedForeachValues = new[] { "a=1", "b=2", "x=1", "x=2" };

        [TestMethod]
        public void Foreach_OnlyNonUniqueHeaders_YieldsAllValuesAcrossAllNames()
        {
            var headers = new HeaderCollection();
            headers.AddHeader("Set-Cookie", "a=1");
            headers.AddHeader("Set-Cookie", "b=2");
            headers.AddHeader("X-Also-Multi", "x=1");
            headers.AddHeader("X-Also-Multi", "x=2");

            var values = headers.Select(h => h.Value).ToList();

            CollectionAssert.AreEquivalent(expectedForeachValues, values);
        }

        [TestMethod]
        public void GetEnumerator_ThroughGenericIEnumerableInterface_ProducesSameResultsAsDirectForeach()
        {
            var headers = new HeaderCollection();
            headers.AddHeader("X-A", "1");
            headers.AddHeader("X-B", "2");
            headers.AddHeader("X-B", "3");

            IEnumerable<HttpHeader> asInterface = headers;
            var viaInterface = asInterface.Select(h => h.Value).ToList();

            var viaForeach = new List<string>();
            foreach (var header in headers) viaForeach.Add(header.Value);

            CollectionAssert.AreEqual(viaForeach, viaInterface);
        }

        [TestMethod]
        public void GetEnumerator_ThroughNonGenericIEnumerableInterface_ProducesSameCount()
        {
            var headers = new HeaderCollection();
            headers.AddHeader("X-A", "1");
            headers.AddHeader("X-B", "2");
            headers.AddHeader("X-B", "3");

            IEnumerable asInterface = headers;
            var count = 0;
            foreach (var _ in asInterface) count++;

            Assert.AreEqual(3, count);
        }

        [TestMethod]
        public void Enumerator_MoveNextPastEnd_KeepsReturningFalse()
        {
            var headers = new HeaderCollection();
            headers.AddHeader("X-A", "1");

            using var enumerator = headers.GetEnumerator();

            Assert.IsTrue(enumerator.MoveNext());
            Assert.IsFalse(enumerator.MoveNext());
            Assert.IsFalse(enumerator.MoveNext());
        }

        [TestMethod]
        public void GetAllHeaders_MatchesForeachOrderAndContents()
        {
            var headers = new HeaderCollection();
            headers.AddHeader("X-Unique", "u1");
            headers.AddHeader("X-Multi", "m1");
            headers.AddHeader("X-Multi", "m2");

            var viaForeach = new List<string>();
            foreach (var header in headers) viaForeach.Add(header.Value);

            var viaGetAllHeaders = headers.GetAllHeaders().Select(h => h.Value).ToList();

            CollectionAssert.AreEqual(viaForeach, viaGetAllHeaders);
        }
    }
}
