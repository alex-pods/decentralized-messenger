using System.Security.Cryptography;
using Dapper;
using Microsoft.Data.Sqlite;

namespace MessengerBackend.Infrastructure;

internal static class FileEncryptionMigration
{
    public static async Task Apply(SqliteConnection c, string filesRoot, bool legacy)
    {
        var paths = c.Query<string>("SELECT path_to_file FROM attachments").Concat(
            c.Query<string>("SELECT path_to_avatar_file FROM users WHERE path_to_avatar_file IS NOT NULL")
                .Select(p => Core.Files.AvatarAbsPath(filesRoot, p)))
            .Concat(Directory.EnumerateFiles(filesRoot, "*", SearchOption.AllDirectories)).Distinct();
        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;
            var data = await File.ReadAllBytesAsync(path);
            var entry = c.QuerySingleOrDefault<Entry>(
                "SELECT source_hash AS SourceHash, completed AS Completed FROM storage_file_migrations WHERE path=@path", new { path });
            if (entry?.Completed == 1)
            {
                StorageEncryption.UnprotectFile(data);
                continue;
            }
            var hash = Convert.ToHexString(SHA256.HashData(data));
            if (entry is null)
            {
                // Existing 4.1 databases have authenticated envelopes. Fresh v4 files are arbitrary
                // plaintext: their first bytes may coincidentally equal the envelope magic.
                if (!legacy && StorageEncryption.HasFileEnvelope(data))
                {
                    StorageEncryption.UnprotectFile(data);
                    c.Execute("INSERT INTO storage_file_migrations VALUES(@path,@hash,1)",
                        new { path, hash = StorageEncryption.ProtectFingerprint(hash) });
                    continue;
                }
                c.Execute("INSERT INTO storage_file_migrations VALUES(@path,@hash,0)",
                    new { path, hash = StorageEncryption.ProtectFingerprint(hash) });
            }
            else if (StorageEncryption.ReadFingerprint(entry.SourceHash) != hash)
            {
                // Crash after atomic file replacement but before journal completion.
                StorageEncryption.UnprotectFile(data);
                c.Execute("UPDATE storage_file_migrations SET completed=1 WHERE path=@path", new { path });
                continue;
            }
            await StorageEncryption.WriteFile(path, data);
            c.Execute("UPDATE storage_file_migrations SET completed=1 WHERE path=@path", new { path });
        }
    }

    private sealed class Entry
    {
        public string SourceHash { get; set; } = "";
        public int Completed { get; set; }
    }
}
