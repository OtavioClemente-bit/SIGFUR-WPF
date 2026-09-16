using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed partial class OpenAiAssistantService
{
    private readonly HttpClient _httpClient;
    private readonly HttpClient _tls12HttpClient;
    private readonly AssistantCredentialService _credentials;
    private readonly AssistantStorageService _storage;
    private readonly AssistantDataService _data;
    private readonly SettingsService _settings;
    private readonly BulletinKnowledgeService _bulletinKnowledge;
    private readonly LogService _log;
    private readonly AssistantKnowledgeContextService _knowledgeContext;
    private readonly SemaphoreSlim _requestGate = new(1, 1);

    public OpenAiAssistantService(
        AssistantCredentialService credentials,
        AssistantStorageService storage,
        AssistantDataService data,
        SettingsService settings,
        BulletinKnowledgeService bulletinKnowledge,
        AssistantKnowledgeContextService knowledgeContext,
        LogService log)
    {
        _credentials = credentials;
        _storage = storage;
        _data = data;
        _settings = settings;
        _bulletinKnowledge = bulletinKnowledge;
        _log = log;
        _knowledgeContext = knowledgeContext;
        _httpClient = CreateHttpClient(forceTls12: false);
        _tls12HttpClient = CreateHttpClient(forceTls12: true);
    }

    private static HttpClient CreateHttpClient(bool forceTls12)
    {
        var client = new HttpClient(CreateHttpHandler(forceTls12)) { Timeout = TimeSpan.FromSeconds(180) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SIGFUR-WPF/6.1");
        return client;
    }

    private static HttpMessageHandler CreateHttpHandler(bool forceTls12)
    {
        // A primeira conexao deixa o Windows negociar o melhor TLS disponivel. A segunda
        // pode fixar TLS 1.2 para proxies antigos, sempre mantendo a validacao da cadeia.
        return new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(45),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            UseProxy = true,
            DefaultProxyCredentials = CredentialCache.DefaultNetworkCredentials,
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = forceTls12 ? SslProtocols.Tls12 : SslProtocols.None,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }
        };
    }

    public async Task TestAsync(AssistantSettings settings, string? temporaryApiKey = null, CancellationToken cancellationToken = default)
    {
        var provider = AssistantCredentialService.DisplayName(settings.Provider);
        var apiKey = string.IsNullOrWhiteSpace(temporaryApiKey) ? _credentials.ReadApiKey(provider) : temporaryApiKey.Trim();
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("Informe ou salve uma chave da API antes de testar.");
        var baseUrl = settings.ApiBaseUrl.Trim().TrimEnd('/');
        var baseUri = ValidateOfficialEndpoint(provider, baseUrl);

        // Validar a chave nao exige uma geracao completa. Esta consulta e pequena,
        // nao consome tokens e responde melhor em redes corporativas.
        var endpoint = new Uri(baseUri.ToString().TrimEnd('/') + "/models");
        Exception? lastNetworkError = null;
        var clients = settings.UseTls12Compatibility
            ? new[] { (Client: _tls12HttpClient, Tls12: true), (Client: _httpClient, Tls12: false) }
            : new[] { (Client: _httpClient, Tls12: false), (Client: _tls12HttpClient, Tls12: true) };
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Headers.Add("X-Client-Request-Id", Guid.NewGuid().ToString("N"));
                var connection = clients[attempt - 1];
                using var response = await connection.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    if (settings.UseTls12Compatibility != connection.Tls12)
                    {
                        settings.UseTls12Compatibility = connection.Tls12;
                        await _storage.SaveSettingsAsync(settings);
                    }
                    return;
                }

                var message = ExtractApiError(body);
                throw response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => new InvalidOperationException("Chave da API invalida ou sem permissao."),
                    HttpStatusCode.TooManyRequests when message.Contains("quota", StringComparison.OrdinalIgnoreCase)
                        => new InvalidOperationException("A chave foi reconhecida, mas a conta da API esta sem saldo ou atingiu o limite de uso."),
                    HttpStatusCode.TooManyRequests => new InvalidOperationException($"A {provider} atingiu um limite temporario. Aguarde um pouco e tente novamente."),
                    _ => new InvalidOperationException($"Falha na {provider} ({(int)response.StatusCode}): {message}")
                };
            }
            catch (Exception ex) when (ShouldWrapNetworkException(ex, cancellationToken))
            {
                lastNetworkError = ex;
                var mode = clients[attempt - 1].Tls12 ? "TLS 1.2" : "TLS automatico do Windows";
                await _log.WriteAsync($"Falha no teste leve da {provider} com {mode} (tentativa {attempt}/2).", ex);
                if (attempt < 2) await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }

        throw BuildFriendlyNetworkException(lastNetworkError ?? new TimeoutException("A rede nao respondeu."));
    }

    private async Task<JsonObject> PostResponsesAsync(AssistantSettings settings, string apiKey, JsonObject payload, CancellationToken cancellationToken)
    {
        var baseUrl = settings.ApiBaseUrl.Trim().TrimEnd('/');
        var provider = AssistantCredentialService.DisplayName(settings.Provider);
        var baseUri = ValidateOfficialEndpoint(provider, baseUrl);

        var endpoint = new Uri(baseUri.ToString().TrimEnd('/') + "/responses");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Headers.Add("X-Client-Request-Id", Guid.NewGuid().ToString("N"));
            request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

            var client = settings.UseTls12Compatibility ? _tls12HttpClient : _httpClient;
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                await _log.WriteAsync($"{provider} Responses API retornou HTTP {(int)response.StatusCode}");
                throw new InvalidOperationException("A solicitação à API falhou: " + ExtractApiError(body));
            }

            return JsonNode.Parse(body) as JsonObject ?? throw new InvalidOperationException($"Resposta inválida recebida da {provider} Responses API.");
        }
        catch (Exception ex) when (ShouldWrapNetworkException(ex, cancellationToken))
        {
            await _log.WriteAsync($"Falha de conexão na {provider} Responses API.", ex);
            throw BuildFriendlyNetworkException(ex);
        }
    }

    private static Uri ValidateOfficialEndpoint(string provider, string baseUrl)
    {
        var expectedHost = AssistantCredentialService.IsDeepSeek(provider) ? "api.deepseek.com" : "api.openai.com";
        var expectedUrl = AssistantCredentialService.IsDeepSeek(provider) ? "https://api.deepseek.com" : "https://api.openai.com/v1";
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals(expectedHost, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)))
            throw new InvalidOperationException($"Por segurança, use o endereço oficial {expectedUrl} para a {provider}.");
        return uri;
    }

    private static bool ShouldWrapNetworkException(Exception ex, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return false;
        if (ex is HttpRequestException) return true;
        if (ex is TaskCanceledException or TimeoutException) return true;
        return HasInner<AuthenticationException>(ex) || HasInner<System.Net.Sockets.SocketException>(ex);
    }

    private static bool HasInner<T>(Exception ex) where T : Exception
    {
        for (var current = ex; current is not null; current = current.InnerException!)
            if (current is T) return true;
        return false;
    }

    public static string FriendlyNetworkMessage(Exception ex)
    {
        var detail = DeepestMessage(ex);
        if (ex is TaskCanceledException or TimeoutException)
            return "A IA demorou demais para responder e o SIGFUR cancelou a tentativa para não travar a tela. Verifique a internet e tente novamente.";

        var text = ex.ToString();
        if (text.Contains("SSL", StringComparison.OrdinalIgnoreCase)
            || text.Contains("TLS", StringComparison.OrdinalIgnoreCase)
            || HasInner<AuthenticationException>(ex))
        {
            return "A conexão HTTPS com a API falhou. Verifique os certificados e o proxy da rede com a TI. O SIGFUR nao desativa a validacao de seguranca da chave. Solicite a instalacao do certificado raiz oficial da rede neste Windows ou a liberacao do domínio oficial do provedor sem inspecao SSL, na porta 443. Confira tambem data e hora do computador. Detalhe tecnico: " + detail;
        }

        return "Não foi possível conectar à IA agora. Verifique internet, proxy/firewall e chave da API. Detalhe técnico: " + detail;
    }

    private static InvalidOperationException BuildFriendlyNetworkException(Exception ex)
        => new(FriendlyNetworkMessage(ex), ex);

    private static string DeepestMessage(Exception ex)
    {
        var current = ex;
        while (current.InnerException is not null) current = current.InnerException;
        return string.IsNullOrWhiteSpace(current.Message) ? ex.Message : current.Message;
    }

    private static string ExtractApiError(string body)
    {
        try
        {
            var root = JsonNode.Parse(body);
            return root?["error"]?["message"]?.GetValue<string>() ?? body;
        }
        catch { return string.IsNullOrWhiteSpace(body) ? "Erro sem detalhes." : body; }
    }

}
