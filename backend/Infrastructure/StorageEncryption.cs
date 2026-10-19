using System.Text;
using Dapper;
using Microsoft.AspNetCore.DataProtection;

namespace MessengerBackend.Infrastructure;

/// <summary>Authenticated encryption at rest. The server can decrypt; this is not E2EE.</summary>
public static class StorageEncryption
{
    private static IDataProtector _text = null!;
    private static IDataProtector _files = null!;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("MSGFILE1\0");

    public static void Configure(IServiceProvider services)
    {
        var provider = services.GetRequiredService<IDataProtectionProvider>();
        _text = provider.CreateProtector("Messenger.Storage.Text.v1");
        _files = provider.CreateProtector("Messenger.Storage.Files.v1");
    }

    public static string Protect(string value) => _text.Protect(value);
    public static string Unprotect(string value) => _text.Unprotect(value);
    public static string ProtectFingerprint(string value) => "dp1:" + Protect(value);
    public static string ReadFingerprint(string value) => value.StartsWith("dp1:", StringComparison.Ordinal)
        ? Unprotect(value[4..]) : value;

    public static async Task WriteFile(string path, byte[] data)
    {
        var payload = Magic.Concat(_files.Protect(data)).ToArray();
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, payload);
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal static bool HasFileEnvelope(byte[] data) => data.AsSpan().StartsWith(Magic);

    internal static byte[] UnprotectFile(byte[] data)
    {
        if (!HasFileEnvelope(data))
            throw new InvalidDataException("Unencrypted or invalid stored file");
        return _files.Unprotect(data[Magic.Length..]);
    }

    public static byte[] ReadFile(string path) => UnprotectFile(File.ReadAllBytes(path));

    // Startup runs before accepting traffic. Resume-safe if interrupted between files or batches.
    public static async Task Migrate(string filesRoot)
    {
        using var c = Db.Open();
        var probe = c.ExecuteScalar<string?>("SELECT key_probe FROM storage_metadata WHERE id=1");
        if (probe is null)
            c.Execute("INSERT INTO storage_metadata(id,key_probe,legacy_files) VALUES(1,@p,1)", new { p = Protect("messenger-storage-v1") });
        else if (Unprotect(probe) != "messenger-storage-v1")
            throw new InvalidOperationException("Invalid storage key probe");

        var changed = false;
        while (true)
        {
            var rows = c.Query<(long Id, string Text)>("SELECT id,text FROM messages WHERE storage_version=0 AND text<>'' LIMIT 200").ToList();
            if (rows.Count == 0) break;
            changed = true;
            SchemaMigrator.Transaction(c, () =>
            {
                foreach (var row in rows)
                    c.Execute("UPDATE messages SET text=@t,storage_version=1 WHERE id=@i", new { t = Protect(row.Text), i = row.Id });
                return 0;
            });
        }
        while (true)
        {
            var rows = c.Query<(long Id, string Bio)>("SELECT id,bio FROM users WHERE storage_version=0 AND bio IS NOT NULL LIMIT 200").ToList();
            if (rows.Count == 0) break;
            changed = true;
            SchemaMigrator.Transaction(c, () =>
            {
                foreach (var row in rows)
                    c.Execute("UPDATE users SET bio=@b,storage_version=1 WHERE id=@i", new { b = Protect(row.Bio), i = row.Id });
                return 0;
            });
        }
        while (true)
        {
            var rows = c.Query<(long Id, string Payload)>("SELECT id,client_ref_payload FROM messages WHERE client_ref_payload IS NOT NULL AND client_ref_payload NOT LIKE 'dp1:%' LIMIT 200").ToList();
            if (rows.Count == 0) break;
            changed = true;
            SchemaMigrator.Transaction(c, () =>
            {
                foreach (var row in rows)
                    c.Execute("UPDATE messages SET client_ref_payload=@p WHERE id=@i", new { p = ProtectFingerprint(row.Payload), i = row.Id });
                return 0;
            });
        }
        var firstMigration = c.ExecuteScalar<long>("SELECT COUNT(*) FROM storage_maintenance WHERE id='001_encryption'") == 0;
        if (firstMigration)
            await FileEncryptionMigration.Apply(c, filesRoot, c.ExecuteScalar<int>("SELECT legacy_files FROM storage_metadata WHERE id=1") == 1);
        changed |= c.Execute("UPDATE messages SET text='',storage_version=0 WHERE deleted_at IS NOT NULL AND text<>''") > 0;
        // Compact only after a migration; ordinary restarts must not rewrite the entire DB or files.
        if (firstMigration || changed)
        {
            c.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
            c.Execute("VACUUM");
            c.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
            c.Execute("INSERT OR REPLACE INTO storage_maintenance VALUES('001_encryption',@at)", new { at = Util.NowIso() });
        }
    }
}
