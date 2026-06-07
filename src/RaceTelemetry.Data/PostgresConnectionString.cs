using Npgsql;

namespace RaceTelemetry.Data;

public static class PostgresConnectionString
{
    public static string Normalize(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != "postgresql" && uri.Scheme != "postgres"))
        {
            return value;
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? 5432 : uri.Port,
            Database = uri.AbsolutePath.TrimStart('/'),
            Username = Uri.UnescapeDataString(uri.UserInfo.Split(':', 2)[0])
        };

        var userInfoParts = uri.UserInfo.Split(':', 2);
        if (userInfoParts.Length == 2)
        {
            builder.Password = Uri.UnescapeDataString(userInfoParts[1]);
        }

        var query = ParseQuery(uri.Query);
        if (bool.TryParse(query.GetValueOrDefault("sslmode"), out var sslModeEnabled) && sslModeEnabled)
        {
            builder.SslMode = SslMode.Require;
        }
        else if (query.GetValueOrDefault("sslmode") is { } sslMode)
        {
            builder.SslMode = Enum.TryParse<SslMode>(sslMode.Replace("-", string.Empty), true, out var parsed)
                ? parsed
                : builder.SslMode;
        }

        return builder.ConnectionString;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        return query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0]).ToLowerInvariant(),
                parts => Uri.UnescapeDataString(parts[1]),
                StringComparer.OrdinalIgnoreCase);
    }
}
