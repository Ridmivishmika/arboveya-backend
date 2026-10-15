namespace Arboveya.Api.Extensions;

public static class ConfigurationExtensions
{
    /// <summary>
    /// Resolves configuration value by checking environment variables first,
    /// and expanding placeholders like ${ENV_NAME} or %ENV_NAME% if present in configuration.
    /// </summary>
    public static string? ResolveEnv(this IConfiguration configuration, string key, params string[] envFallbacks)
    {
        // 1. Check explicit environment variable fallbacks first
        foreach (var envVar in envFallbacks)
        {
            var envVal = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrWhiteSpace(envVal))
            {
                return envVal.Trim();
            }
        }

        // 2. Fetch value from IConfiguration
        var val = configuration[key];
        if (string.IsNullOrWhiteSpace(val))
        {
            return null;
        }

        val = val.Trim();

        // 3. Resolve ${VAR_NAME} format
        if (val.StartsWith("${") && val.EndsWith('}') && val.Length > 3)
        {
            var inner = val[2..^1].Trim();
            var resolved = Environment.GetEnvironmentVariable(inner)
                ?? Environment.GetEnvironmentVariable(inner.Replace(":", "__"));
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved.Trim();
            }
        }

        // 4. Resolve %VAR_NAME% format
        if (val.StartsWith('%') && val.EndsWith('%') && val.Length > 2)
        {
            var inner = val[1..^1].Trim();
            var resolved = Environment.GetEnvironmentVariable(inner)
                ?? Environment.GetEnvironmentVariable(inner.Replace(":", "__"));
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved.Trim();
            }
        }

        // 5. If val directly matches an environment variable name
        var direct = Environment.GetEnvironmentVariable(val)
            ?? Environment.GetEnvironmentVariable(val.Replace(":", "__"));
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct.Trim();
        }

        return val;
    }
}
