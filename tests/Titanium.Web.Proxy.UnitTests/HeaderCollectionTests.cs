using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.UnitTests
{
    /// <summary>
    ///     Phase 0A characterization tests for <see cref="HeaderCollection" />.
    ///     HeaderCollection underpins the new Phase 1 <c>TrailingHeaders</c> property, so its core
    ///     unique/non-unique/add/remove/set semantics need a documented baseline before it gains new callers.
    /// </summary>
    [TestClass]
    public class HeaderCollectionTests
    {
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
            CollectionAssert.AreEquivalent(new[] { "1", "2" }, all.ConvertAll(h => h.Value));
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
            Assert.AreEqual(1, headers.GetHeaders("Content-Type").Count);
        }

        [TestMethod]
        public void SetOrAddHeaderValue_NullValue_RemovesHeader()
        {
            var headers = new HeaderCollection();
            headers.AddHeader(KnownHeaders.ContentType, "text/plain");

            headers.SetOrAddHeaderValue(KnownHeaders.ContentType, (string)null);

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
    }
}
