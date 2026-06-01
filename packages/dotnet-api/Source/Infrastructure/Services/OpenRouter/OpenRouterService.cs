using OpenRouter.NET;

namespace Source.Infrastructure.Services.OpenRouter;

public class OpenRouterService
{
    private readonly ILogger<OpenRouterService> _logger;
    private readonly OpenRouterClient _client;
    private readonly string _apiKey;

    public OpenRouterService(
        IConfiguration configuration,
        ILogger<OpenRouterService> logger)
    {
        _logger = logger;

        _apiKey = configuration["OpenRouter:ApiKey"]
            ?? Environment.GetEnvironmentVariable("OpenRouter__ApiKey")
            ?? throw new InvalidOperationException("OpenRouter API key is required (OpenRouter:ApiKey or OpenRouter__ApiKey)");

        _client = new OpenRouterClient(_apiKey);

        _logger.LogInformation("OpenRouterService initialized with configured API key");
    }

    public OpenRouterClient Client => _client;

    public string ApiKey => _apiKey;

    /// <summary>
    /// Creates a new OpenRouterClient instance (useful for registering tools per-request)
    /// </summary>
    public OpenRouterClient CreateClient() => new OpenRouterClient(_apiKey);
}

