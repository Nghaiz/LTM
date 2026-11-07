using System;
using System.IO;
using Ironfront.MasterServer.Data;
using Xunit;

namespace Ironfront.MasterServer.Tests
{
    /// <summary>
    /// A disposed <see cref="SqliteDatabase"/> leaves no file open.
    /// </summary>
    /// <remarks>
    /// The test harness deletes its database when it is disposed, and each master test run left
    /// about 110 <c>ironfront-*.db</c>, <c>-wal</c> and <c>-shm</c> files in the temp directory
    /// anyway: the connection went back to Microsoft.Data.Sqlite's pool with the file open, and
    /// the delete's <c>IOException</c> was swallowed. Only Windows refuses to delete an open
    /// file, so this test catches it there (CI runs <c>windows-latest</c>); on Linux it passes
    /// either way.
    /// </remarks>
    public sealed class SqliteDatabaseFileReleaseTests
    {
        [Fact]
        public void ADisposedDatabaseCanBeDeleted()
        {
            string path = Path.Combine(Path.GetTempPath(), $"ironfront-release-{Guid.NewGuid():N}.db");
            new SqliteDatabase(path).Dispose();

            foreach (string suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                string file = path + suffix;
                Exception? error = Record.Exception(() =>
                {
                    if (File.Exists(file)) File.Delete(file);
                });

                Assert.True(
                    error is null,
                    $"{Path.GetFileName(file)} is still open after SqliteDatabase.Dispose, so it can "
                    + $"never be deleted while the process runs: {error?.Message}");
            }
        }
    }
}
