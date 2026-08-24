using SPTarkov.Common.Models.Logging;

namespace Vagabond.Server.Services;

public class VagabondLogger
{
    private static ISptLogger<VagabondLoader>? _logger;

    public static void Init(ISptLogger<VagabondLoader> logger)
    {
        _logger = logger;
    }

    public static void Log(string message)
    {
        _logger?.Info($"[Vagabond] {message}");
    }

    public static void Error(string message)
    {
        _logger?.Error($"[Vagabond] {message}");
    }

    public static void Warning(string message)
    {
        _logger?.Warning($"[Vagabond] {message}");
    }

    public static void Success(string message)
    {
        _logger?.Success($"[Vagabond] {message}");
    }
}