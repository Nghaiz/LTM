using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Ironfront.Net.Configuration.Tests
{
    /// <summary>
    /// The <c>.env</c> reader behind phase-00 objective 6. The parse tests operate on line
    /// lists and never touch the process environment, so they are order-independent and safe
    /// to run in parallel with everything else.
    /// </summary>
    public class DotEnvTests
    {
        private static string? Value(IReadOnlyList<KeyValuePair<string, string>> parsed, string key)
        {
            foreach (KeyValuePair<string, string> pair in parsed)
                if (pair.Key == key)
                    return pair.Value;
            return null;
        }

        [Fact]
        public void CommentsAndBlankLinesAreSkipped()
        {
            var parsed = DotEnv.Parse(new[]
            {
                "# a comment",
                "",
                "   ",
                "IRONFRONT_MASTER_PORT=27000",
            });

            Assert.Single(parsed);
            Assert.Equal("27000", Value(parsed, "IRONFRONT_MASTER_PORT"));
        }

        [Fact]
        public void SurroundingQuotesAreStrippedButInnerCharactersAreKept()
        {
            var parsed = DotEnv.Parse(new[]
            {
                "DOUBLE=\"hello world\"",
                "SINGLE='hello world'",
            });

            Assert.Equal("hello world", Value(parsed, "DOUBLE"));
            Assert.Equal("hello world", Value(parsed, "SINGLE"));
        }

        [Fact]
        public void AnEqualsInsideTheValueIsPreserved()
        {
            // A base64 secret ends in '=' padding: splitting on the last '=' instead of the
            // first would corrupt exactly the value this whole mechanism exists to carry.
            var parsed = DotEnv.Parse(new[] { "IRONFRONT_SHARED_SECRET=YWJjZGVm==" });

            Assert.Equal("YWJjZGVm==", Value(parsed, "IRONFRONT_SHARED_SECRET"));
        }

        [Fact]
        public void LinesWithoutAKeyOrSeparatorAreIgnored()
        {
            var parsed = DotEnv.Parse(new[]
            {
                "no separator here",
                "=orphan value",
                "GOOD=1",
            });

            Assert.Single(parsed);
            Assert.Equal("1", Value(parsed, "GOOD"));
        }

        [Fact]
        public void LoadingAMissingFileIsNotAnErrorAndAppliesNothing()
        {
            string absent = Path.Combine(Path.GetTempPath(), "ironfront-" + Guid.NewGuid().ToString("N") + ".env");

            Assert.False(File.Exists(absent));
            Assert.Equal(0, DotEnv.Load(absent));
        }
    }
}
