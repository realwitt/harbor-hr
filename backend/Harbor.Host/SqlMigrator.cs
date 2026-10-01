using Npgsql;

namespace Harbor.Host;

// Applies db/*.sql with the owner connection. The app connection is not used here.
public static class SqlMigrator
{
    public static void Apply(string ownerConnectionString, string sqlDirectory, ILogger logger)
    {
        var files = Directory.GetFiles(sqlDirectory, "*.sql")
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0)
        {
            throw new InvalidOperationException($"No SQL files in {sqlDirectory}.");
        }

        using var connection = new NpgsqlConnection(ownerConnectionString);
        connection.Open();

        using (var setup = connection.CreateCommand())
        {
            setup.CommandText =
                """
                create schema if not exists meta;
                create table if not exists meta.schema_migration (
                  filename text primary key,
                  applied_at timestamptz not null default now()
                );
                """;
            setup.ExecuteNonQuery();
        }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            using var transaction = connection.BeginTransaction();

            using var check = connection.CreateCommand();
            check.Transaction = transaction;
            check.CommandText = "select 1 from meta.schema_migration where filename = @name";
            check.Parameters.AddWithValue("name", name);
            if (check.ExecuteScalar() is not null)
            {
                transaction.Commit();
                logger.LogInformation("SQL already applied: {File}", name);
                continue;
            }

            using var apply = connection.CreateCommand();
            apply.Transaction = transaction;
            apply.CommandText = File.ReadAllText(file);
            apply.ExecuteNonQuery();

            using var mark = connection.CreateCommand();
            mark.Transaction = transaction;
            mark.CommandText = "insert into meta.schema_migration (filename) values (@name)";
            mark.Parameters.AddWithValue("name", name);
            mark.ExecuteNonQuery();
            transaction.Commit();
            logger.LogInformation("Applied SQL: {File}", name);
        }
    }

    public static string FindSqlDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "db"),
            Path.Combine(Directory.GetCurrentDirectory(), "db"),
        };

        foreach (var candidate in candidates)
        {
            var full = Path.GetFullPath(candidate);
            if (Directory.Exists(full) && Directory.EnumerateFiles(full, "*.sql").Any())
            {
                return full;
            }
        }

        throw new InvalidOperationException("SQL directory db/ was not found.");
    }
}
