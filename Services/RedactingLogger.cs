using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gelato.Services;

/// <summary>Redacts remote addresses before plugin log events reach any logging provider.</summary>
public static class SafeLog
{
    // Paths, user info, queries and fragments can all contain credentials. Do not keep
    // selected path segments or rely on a list of known addon parameter names.
    private static readonly Regex Url = new(
        @"\b(?:https?|ftp|file|gelato|magnet)(?::|%3a)(?://|\\/\\/|%2f%2f)?[^\s<>""']*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static string Redact(string value) => Url.Replace(value, "[redacted-url]");

    public static void Register(IServiceCollection services)
    {
        // Register closed logger types from this plugin only; Jellyfin and other plugins
        // keep their original logging services and filtering configuration.
        var loggerTypes = typeof(SafeLog).Assembly.GetTypes()
            .SelectMany(type => type.GetConstructors())
            .SelectMany(ctor => ctor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .Where(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ILogger<>))
            .Distinct();
        foreach (var type in loggerTypes)
        {
            var wrapper = typeof(RedactingLogger<>).MakeGenericType(type.GenericTypeArguments);
            services.AddSingleton(type, sp => Activator.CreateInstance(wrapper,
                sp.GetRequiredService<ILoggerFactory>())!);
        }
    }
}

public sealed class RedactingLogger<T>(ILoggerFactory factory) : ILogger<T>
{
    private readonly ILogger inner = factory.CreateLogger<T>();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
        inner.BeginScope(SafeLog.Redact(state.ToString() ?? string.Empty));

    public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var message = SafeLog.Redact(formatter(state, exception));
        // Exception messages, inner exceptions and stack traces may embed authenticated
        // URLs or response bodies. Keep the diagnostic type, never the original object.
        if (exception is not null) message += $" (error: {exception.GetType().Name})";
        // Do not forward the original structured state: providers can persist its raw
        // values independently of the formatted, redacted message.
        inner.Log(logLevel, eventId, message, null, static (value, _) => value);
    }
}
