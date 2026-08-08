using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MeetingTranscriber.App.Services;

/// <summary>A chat completion request for an OpenAI-compatible API.</summary>
public sealed record ChatMessage(string Role, string Content);

/// <summary>
/// Minimal OpenAI-compatible API client (chat completions, model listing).
/// Works against local servers (llama.cpp, Ollama, vLLM, LM Studio) and hosted
/// APIs (OpenAI, OpenRouter, ...) alike.
/// </summary>
public sealed class ApiClient
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(120) };

    private readonly ApiSettings _settings;

    public ApiClient(ApiSettings settings)
    {
        _settings = settings;
    }

    /// <summary>GET /models — used to validate the endpoint and populate the model dropdown.</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            _settings.BaseUrl.TrimEnd('/') + "/models"
        );
        ApplyAuth(request);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var models = new List<string>();
        if (
            doc.RootElement.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Array
        )
        {
            foreach (var item in data.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var id))
                    models.Add(id.GetString() ?? "");
            }
        }
        return models.Where(m => !string.IsNullOrWhiteSpace(m)).ToList();
    }

    /// <summary>POST /chat/completions for a single assistant turn.</summary>
    public async Task<string> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        int maxTokens,
        CancellationToken ct = default
    )
    {
        var payload = new
        {
            model = _settings.Model,
            messages,
            max_tokens = maxTokens,
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            _settings.BaseUrl.TrimEnd('/') + "/chat/completions"
        );
        ApplyAuth(request);
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json"
        );

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        if (
            root.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
        )
        {
            foreach (var choice in choices.EnumerateArray())
            {
                if (
                    choice.TryGetProperty("message", out var message)
                    && message.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.String
                )
                {
                    return content.GetString() ?? "";
                }
            }
        }
        throw new InvalidOperationException("API response contained no message content.");
    }

    private void ApplyAuth(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_settings.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                _settings.ApiKey
            );
    }
}
