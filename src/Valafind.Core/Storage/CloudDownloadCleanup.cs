using Microsoft.Data.Sqlite;
using Valafind.Core.Platform;

namespace Valafind.Core.Storage;

/// <summary>What <see cref="CloudDownloadCleanup.ReleaseAll(string)"/> did.</summary>
/// <param name="Released">Requests undone (files given back, or nothing left to undo).</param>
/// <param name="Kept">Requests that could not be undone now; they stay recorded, so a later Valafind undoes them.</param>
/// <param name="Error">Why the recorded requests could not be read, or null.</param>
public sealed record CloudCleanupResult(int Released, int Kept, string? Error = null);

/// <summary>
/// Undoes every online-only cloud file download Valafind requested and has not given back yet (see the
/// <c>cloud_downloads</c> table of <see cref="SqliteIndexStore"/>). Used when Valafind is uninstalled, after the
/// running copy has quit: otherwise those files would stay pinned and downloaded, since no later run would give them
/// back. The same safety rules apply as during indexing (<see cref="ICloudFiles.Release"/>): a file is made
/// online-only only when that cannot lose anything. The index itself is left as it is.
/// </summary>
public static class CloudDownloadCleanup
{
    private const int BusyTimeoutMs = 10_000;

    /// <summary>Undoes the requests recorded in the index database at <paramref name="databasePath"/>. Never throws.</summary>
    public static CloudCleanupResult ReleaseAll(string databasePath) => ReleaseAll(databasePath, CloudFiles.Current);

    internal static CloudCleanupResult ReleaseAll(string databasePath, ICloudFiles cloud)
    {
        ArgumentNullException.ThrowIfNull(databasePath);
        if (!File.Exists(databasePath))
            return new CloudCleanupResult(0, 0);
        try
        {
            // Opened directly rather than through SqliteIndexStore.Open, which would reset the index for another model.
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
            db.Open();
            using (var pragma = db.CreateCommand())
            {
                pragma.CommandText = $"PRAGMA busy_timeout = {BusyTimeoutMs};";
                pragma.ExecuteNonQuery();
            }
            if (!HasJournal(db))
                return new CloudCleanupResult(0, 0);

            var rows = new List<(string Key, string Path, CloudMark Mark, DateTimeOffset RequestedUtc)>();
            using (var read = db.CreateCommand())
            {
                read.CommandText = "SELECT path_key, path, state, file_id, requested_utc FROM cloud_downloads ORDER BY requested_utc;";
                using var reader = read.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add((reader.GetString(0), reader.GetString(1), new CloudMark(reader.GetInt32(2), reader.GetInt64(3)),
                        new DateTimeOffset(reader.GetInt64(4), TimeSpan.Zero)));
                }
            }

            int released = 0, kept = 0;
            foreach (var (key, path, mark, requestedUtc) in rows)
            {
                bool done;
                try
                {
                    done = cloud.Release(path, mark, requestedUtc, final: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    done = false;
                }
                if (!done)
                {
                    kept++;
                    continue;
                }
                using var delete = db.CreateCommand();
                delete.CommandText = "DELETE FROM cloud_downloads WHERE path_key = $key;";
                delete.Parameters.AddWithValue("$key", key);
                delete.ExecuteNonQuery();
                released++;
            }
            return new CloudCleanupResult(released, kept);
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new CloudCleanupResult(0, 0, e.Message);
        }
    }

    private static bool HasJournal(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'cloud_downloads';";
        return cmd.ExecuteScalar() is not null;
    }
}
