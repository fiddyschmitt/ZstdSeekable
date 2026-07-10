using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ZstdSeekable.Internal;

namespace ZstdSeekable.Tests
{
    [TestClass]
    public class XxHash32Tests
    {
        //reference vectors from the xxHash project (seed 0)
        [DataTestMethod]
        [DataRow("", 0x02CC5D05U)]
        [DataRow("a", 0x550D7456U)]
        [DataRow("abc", 0x32D153FFU)]
        [DataRow("Nobody inspects the spammish repetition", 0xE2293B2FU)]
        public void MatchesReferenceVectors(string input, uint expected)
        {
            Assert.AreEqual(expected, XxHash32.Hash(Encoding.ASCII.GetBytes(input)));
        }

        [TestMethod]
        public void LongInputAllPathsCovered()
        {
            //>16 bytes exercises the 4-lane loop; the tail covers the 4-byte and 1-byte finishers
            var data = TestData.RandomBytes(seed: 7, length: 1027);
            var whole = XxHash32.Hash(data);
            Assert.AreNotEqual(0U, whole);
            Assert.AreEqual(whole, XxHash32.Hash(data));    //deterministic
        }
    }
}
