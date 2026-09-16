using Microsoft.Win32;
using System.Diagnostics.Tracing;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace SIGFUR.Wpf.Services;

public sealed record OpenAiNetworkDiagnosticResult(string ReportPath, string Summary);

public static class OpenAiNetworkDiagnostic
{
    private static readonly Uri Endpoint = new("https://api.openai.com/v1/models");
    private static readonly TimeSpan StageTimeout = TimeSpan.FromSeconds(20);

    public static async Task<OpenAiNetworkDiagnosticResult> RunAsync(
        AppPaths paths,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return await RunAsync(paths.LogDirectory, progress, cancellationToken);
    }

    public static async Task<OpenAiNetworkDiagnosticResult> RunAsync(
        string logDirectory,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        Directory.CreateDirectory(logDirectory);
        var startedAt = DateTimeOffset.Now;
        var totalClock = Stopwatch.StartNew();
        var report = new StringBuilder();
        Header(report, "DIAGNÓSTICO DE REDE OPENAI — SIGFUR");
        Line(report, "Início", startedAt.ToString("O"));
        Line(report, "Destino", Endpoint.ToString());
        Line(report, "Observação de segurança", "nenhuma chave da API é lida ou enviada; HTTP 401 é o resultado esperado");
        Line(report, "Validação TLS", "permanece ativa; nenhum certificado inválido é aceito");
        Line(report, "Timeout por etapa", StageTimeout.ToString());

        progress?.Report("Coletando ambiente, proxy e Schannel...");
        AppendRuntime(report);
        await AppendProxyConfigurationAsync(report, cancellationToken);
        AppendSchannelConfiguration(report);
        AppendSecuritySoftware(report);
        await AppendCurlControlAsync(report, cancellationToken);

        progress?.Report("Resolvendo DNS de api.openai.com...");
        await RunDnsProbeAsync(report, cancellationToken);

        progress?.Report("Testando TCP direto na porta 443...");
        await RunTcpProbeAsync(report, cancellationToken);

        progress?.Report("Executando handshake TLS direto...");
        await RunTlsProbeAsync(report, cancellationToken);

        using var networkEvents = new NetworkEventRecorder(totalClock);
        var probes = new[]
        {
            new HttpProbe("A — equivalente ao transporte atual, GET anônimo", true, SslProtocols.None, HttpVersion.Version11, HttpVersionPolicy.RequestVersionOrLower, HttpMethod.Get, false),
            new HttpProbe("B — proxy do sistema + HTTP/1.1 exato", true, SslProtocols.None, HttpVersion.Version11, HttpVersionPolicy.RequestVersionExact, HttpMethod.Get, false),
            new HttpProbe("C — proxy do sistema + HTTP/2 ou inferior", true, SslProtocols.None, HttpVersion.Version20, HttpVersionPolicy.RequestVersionOrLower, HttpMethod.Get, false),
            new HttpProbe("D — acesso direto, sem proxy + HTTP/1.1", false, SslProtocols.None, HttpVersion.Version11, HttpVersionPolicy.RequestVersionExact, HttpMethod.Get, false),
            new HttpProbe("E — proxy do sistema + TLS 1.2 + HTTP/1.1", true, SslProtocols.Tls12, HttpVersion.Version11, HttpVersionPolicy.RequestVersionExact, HttpMethod.Get, false),
            new HttpProbe("F — GET com Authorization fictícia", true, SslProtocols.None, HttpVersion.Version11, HttpVersionPolicy.RequestVersionOrLower, HttpMethod.Get, true),
            new HttpProbe("G — POST JSON com Authorization fictícia", true, SslProtocols.None, HttpVersion.Version11, HttpVersionPolicy.RequestVersionOrLower, HttpMethod.Post, true)
        };

        foreach (var probe in probes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Executando {probe.Name}...");
            await RunHttpProbeAsync(report, probe, cancellationToken);
        }

        Header(report, "EVENTOS INTERNOS SYSTEM.NET");
        report.AppendLine(networkEvents.BuildReport());
        Line(report, "Tempo total", FormatElapsed(totalClock.Elapsed));
        Line(report, "Fim", DateTimeOffset.Now.ToString("O"));

        var reportPath = Path.Combine(
            logDirectory,
            $"openai_network_diagnostic_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        await File.WriteAllTextAsync(reportPath, report.ToString(), Encoding.UTF8, cancellationToken);

        var summary = BuildSummary(report.ToString(), totalClock.Elapsed);
        return new OpenAiNetworkDiagnosticResult(reportPath, summary);
    }

    private static void AppendRuntime(StringBuilder report)
    {
        Header(report, "AMBIENTE");
        Line(report, "SIGFUR", Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "desconhecida");
        Line(report, ".NET", RuntimeInformation.FrameworkDescription);
        Line(report, "Runtime", Environment.Version.ToString());
        Line(report, "Sistema", RuntimeInformation.OSDescription);
        Line(report, "Arquitetura", $"{RuntimeInformation.OSArchitecture} / processo {RuntimeInformation.ProcessArchitecture}");
        Line(report, "Processo", $"{Environment.ProcessPath} (PID {Environment.ProcessId})");
        Line(report, "Self-contained", string.IsNullOrWhiteSpace(Environment.ProcessPath)
            ? "indeterminado"
            : !Path.GetFileNameWithoutExtension(Environment.ProcessPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? "provável (host é o executável do aplicativo)"
                : "não");
        Line(report, "Usuário", $"{Environment.UserDomainName}\\{Environment.UserName}");
        Line(report, "Rede disponível", NetworkInterface.GetIsNetworkAvailable().ToString());
        Line(report, "Data/hora local", DateTimeOffset.Now.ToString("O"));
        Line(report, "Fuso", TimeZoneInfo.Local.DisplayName);

        report.AppendLine("Variáveis de proxy (valores sanitizados):");
        foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "all_proxy", "no_proxy" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (value is not null) report.AppendLine($"  {name}={SanitizeProxyValue(value)}");
        }
    }

    private static async Task AppendProxyConfigurationAsync(StringBuilder report, CancellationToken cancellationToken)
    {
        Header(report, "PROXY");
        try
        {
            var proxyResult = await Task.Run(() =>
            {
                var proxy = HttpClient.DefaultProxy;
                var bypassed = proxy.IsBypassed(Endpoint);
                var selected = proxy.GetProxy(Endpoint);
                var direct = bypassed || selected is null || selected == Endpoint;
                return (Type: proxy.GetType().FullName ?? proxy.GetType().Name, Bypassed: bypassed, Direct: direct, Selected: selected);
            }, cancellationToken).WaitAsync(StageTimeout, cancellationToken);

            Line(report, "HttpClient.DefaultProxy", proxyResult.Type);
            Line(report, "IsBypassed(api.openai.com)", proxyResult.Bypassed.ToString());
            Line(report, "Proxy selecionado", proxyResult.Direct ? "DIRETO" : SanitizeUri(proxyResult.Selected));
        }
        catch (Exception ex)
        {
            report.AppendLine("Falha/timeout ao resolver HttpClient.DefaultProxy (possível PAC/WPAD bloqueado):");
            AppendException(report, ex);
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            Line(report, "WinINET ProxyEnable", RegistryText(key?.GetValue("ProxyEnable")));
            Line(report, "WinINET ProxyServer", SanitizeProxyValue(RegistryText(key?.GetValue("ProxyServer"))));
            Line(report, "WinINET AutoConfigURL", SanitizeProxyValue(RegistryText(key?.GetValue("AutoConfigURL"))));
            Line(report, "WinINET AutoDetect", RegistryText(key?.GetValue("AutoDetect")));
        }
        catch (Exception ex)
        {
            report.AppendLine("Falha ao ler proxy WinINET:");
            AppendException(report, ex);
        }

        try
        {
            var output = await RunProcessAsync("netsh.exe", ["winhttp", "show", "proxy"], TimeSpan.FromSeconds(10), cancellationToken);
            report.AppendLine("WinHTTP (netsh winhttp show proxy):");
            report.AppendLine(Indent(output.Trim(), "  "));
        }
        catch (Exception ex)
        {
            report.AppendLine("Falha ao consultar proxy WinHTTP:");
            AppendException(report, ex);
        }

        report.AppendLine("Nota: SocketsHttpHandler com Proxy=null usa HttpClient.DefaultProxy (variáveis de ambiente ou proxy do usuário);");
        report.AppendLine("      isso não equivale automaticamente ao proxy estático mostrado por 'netsh winhttp'.");
    }

    private static void AppendSchannelConfiguration(StringBuilder report)
    {
        Header(report, "SCHANNEL E CERTIFICADOS LOCAIS");
        foreach (var protocol in new[] { "TLS 1.2", "TLS 1.3" })
        {
            var path = $@"SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\{protocol}\Client";
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path);
                Line(report, $"Schannel {protocol} Client Enabled", RegistryText(key?.GetValue("Enabled")));
                Line(report, $"Schannel {protocol} Client DisabledByDefault", RegistryText(key?.GetValue("DisabledByDefault")));
            }
            catch (Exception ex)
            {
                report.AppendLine($"Falha ao ler {path}:");
                AppendException(report, ex);
            }
        }

        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            try
            {
                using var store = new X509Store(StoreName.Root, location);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                var matches = store.Certificates
                    .Where(IsInspectionCertificate)
                    .Cast<X509Certificate2>()
                    .ToList();
                report.AppendLine($"Raízes que podem pertencer a inspeção HTTPS ({location}): {matches.Count}");
                foreach (var certificate in matches) AppendCertificate(report, certificate, "  ");
            }
            catch (Exception ex)
            {
                report.AppendLine($"Falha ao ler raízes {location}:");
                AppendException(report, ex);
            }
        }
    }

    private static void AppendSecuritySoftware(StringBuilder report)
    {
        Header(report, "SOFTWARE DE SEGURANÇA");
        try
        {
            var processes = Process.GetProcesses()
                .Select(process =>
                {
                    try { return process.ProcessName; }
                    finally { process.Dispose(); }
                })
                .Where(IsSecuritySoftwareName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name)
                .ToList();
            Line(report, "Processos Kaspersky/endpoint detectados", processes.Count == 0 ? "nenhum pelo nome" : string.Join(", ", processes));
        }
        catch (Exception ex)
        {
            report.AppendLine("Falha ao inventariar processos de segurança:");
            AppendException(report, ex);
        }

        foreach (var directory in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Kaspersky Lab"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Kaspersky Lab")
                 }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Line(report, $"Diretório {directory}", Directory.Exists(directory) ? "presente" : "ausente");
        }
        report.AppendLine("A presença do Kaspersky não prova inspeção SSL; o emissor do certificado apresentado nos probes é a evidência relevante.");
    }

    private static async Task AppendCurlControlAsync(StringBuilder report, CancellationToken cancellationToken)
    {
        Header(report, "CONTROLE EXTERNO CURL.EXE");
        try
        {
            var version = await RunProcessAsync("curl.exe", ["--version"], TimeSpan.FromSeconds(10), cancellationToken);
            report.AppendLine(Indent(version.Trim(), "  "));
            var metrics = await RunProcessAsync(
                "curl.exe",
                [
                    "--silent",
                    "--show-error",
                    "--output", "NUL",
                    "--max-time", StageTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture),
                    "--write-out",
                    "http_code=%{http_code} remote_ip=%{remote_ip} http_version=%{http_version} ssl_verify_result=%{ssl_verify_result} time_dns=%{time_namelookup} time_tcp=%{time_connect} time_tls=%{time_appconnect} time_headers=%{time_starttransfer} time_total=%{time_total}",
                    Endpoint.ToString()
                ],
                StageTimeout + TimeSpan.FromSeconds(2),
                cancellationToken);
            Line(report, "Resultado curl sem chave", metrics.Trim());
        }
        catch (Exception ex)
        {
            report.AppendLine("Falha/timeout no controle curl.exe:");
            AppendException(report, ex);
        }
    }

    private static async Task RunDnsProbeAsync(StringBuilder report, CancellationToken cancellationToken)
    {
        Header(report, "ETAPA DNS");
        var clock = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StageTimeout);
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(Endpoint.Host, timeout.Token);
            Line(report, "Resultado", "SUCESSO");
            Line(report, "Tempo DNS", FormatElapsed(clock.Elapsed));
            Line(report, "Endereços", string.Join(", ", addresses.Select(address => $"{address} ({address.AddressFamily})")));
        }
        catch (Exception ex)
        {
            Line(report, "Resultado", timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested ? "TIMEOUT" : "FALHA");
            Line(report, "Tempo até falha/timeout", FormatElapsed(clock.Elapsed));
            AppendException(report, ex);
        }
    }

    private static async Task RunTcpProbeAsync(StringBuilder report, CancellationToken cancellationToken)
    {
        Header(report, "ETAPA TCP DIRETA");
        var clock = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StageTimeout);
        using var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(Endpoint.Host, Endpoint.Port, timeout.Token);
            Line(report, "Resultado", "SUCESSO");
            Line(report, "Tempo TCP", FormatElapsed(clock.Elapsed));
            Line(report, "Endpoint local", tcp.Client.LocalEndPoint?.ToString() ?? "indisponível");
            Line(report, "Endpoint remoto", tcp.Client.RemoteEndPoint?.ToString() ?? "indisponível");
        }
        catch (Exception ex)
        {
            Line(report, "Resultado", timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested ? "TIMEOUT" : "FALHA");
            Line(report, "Tempo até falha/timeout", FormatElapsed(clock.Elapsed));
            AppendException(report, ex);
        }
    }

    private static async Task RunTlsProbeAsync(StringBuilder report, CancellationToken cancellationToken)
    {
        Header(report, "ETAPA TLS DIRETA (SSLSTREAM/SCHANNEL)");
        var clock = Stopwatch.StartNew();
        var certificateReport = new StringBuilder();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StageTimeout);
        using var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(Endpoint.Host, Endpoint.Port, timeout.Token);
            var tcpElapsed = clock.Elapsed;
            await using var ssl = new SslStream(
                tcp.GetStream(),
                leaveInnerStreamOpen: false,
                (_, certificate, chain, errors) =>
                {
                    AppendCertificateValidation(certificateReport, certificate, chain, errors);
                    return errors == SslPolicyErrors.None;
                });

            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = Endpoint.Host,
                EnabledSslProtocols = SslProtocols.None,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11]
            }, timeout.Token);

            Line(report, "Resultado", "SUCESSO");
            Line(report, "Tempo TCP", FormatElapsed(tcpElapsed));
            Line(report, "Tempo TLS após TCP", FormatElapsed(clock.Elapsed - tcpElapsed));
            Line(report, "Tempo acumulado", FormatElapsed(clock.Elapsed));
            Line(report, "TLS negociado", ssl.SslProtocol.ToString());
            Line(report, "ALPN", ssl.NegotiatedApplicationProtocol == default
                ? "não negociado"
                : Encoding.ASCII.GetString(ssl.NegotiatedApplicationProtocol.Protocol.Span));
            Line(report, "Cipher suite", ssl.NegotiatedCipherSuite.ToString());
        }
        catch (Exception ex)
        {
            Line(report, "Resultado", timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested ? "TIMEOUT" : "FALHA");
            Line(report, "Tempo até falha/timeout", FormatElapsed(clock.Elapsed));
            AppendException(report, ex);
        }
        report.Append(certificateReport);
    }

    private static async Task RunHttpProbeAsync(
        StringBuilder report,
        HttpProbe probe,
        CancellationToken cancellationToken)
    {
        Header(report, $"ETAPA HTTP: {probe.Name}");
        var clock = Stopwatch.StartNew();
        var certificateReport = new StringBuilder();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StageTimeout);
        using var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = StageTimeout,
            PooledConnectionLifetime = TimeSpan.Zero,
            PooledConnectionIdleTimeout = TimeSpan.Zero,
            UseProxy = probe.UseProxy,
            DefaultProxyCredentials = CredentialCache.DefaultNetworkCredentials,
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = probe.SslProtocols,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
                {
                    AppendCertificateValidation(certificateReport, certificate, chain, errors);
                    return errors == SslPolicyErrors.None;
                }
            }
        };
        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SIGFUR-WPF-Network-Diagnostic/1.0");

        try
        {
            var requestUri = probe.Method == HttpMethod.Post
                ? new Uri("https://api.openai.com/v1/chat/completions")
                : Endpoint;
            using var request = new HttpRequestMessage(probe.Method, requestUri)
            {
                Version = probe.HttpVersion,
                VersionPolicy = probe.VersionPolicy
            };
            request.Headers.ExpectContinue = false;
            if (probe.SendDummyAuthorization)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "sigfur-diagnostic-invalid-key");
            if (probe.Method == HttpMethod.Post)
                request.Content = new StringContent("""{"model":"sigfur-diagnostic","messages":[]}""", Encoding.UTF8, "application/json");

            Line(report, "UseProxy", probe.UseProxy.ToString());
            Line(report, "Proxy esperado", probe.UseProxy
                ? "HttpClient.DefaultProxy (resultado detalhado na seção PROXY)"
                : "DIRETO (UseProxy=false)");
            Line(report, "TLS solicitado", probe.SslProtocols == SslProtocols.None ? "automático do sistema" : probe.SslProtocols.ToString());
            Line(report, "Método/URI", $"{request.Method} {request.RequestUri}");
            Line(report, "Authorization", probe.SendDummyAuthorization
                ? "Bearer fictício e inválido; a chave real não foi lida"
                : "ausente");
            Line(report, "HTTP solicitado", $"{request.Version} / {request.VersionPolicy}");
            Line(report, "Expect: 100-continue", request.Headers.ExpectContinue?.ToString() ?? "não definido");
            Line(report, "Keep-Alive", "padrão ativo; conexão nova neste probe");
            Line(report, "AutomaticDecompression", handler.AutomaticDecompression.ToString());

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var headersElapsed = clock.Elapsed;
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            Line(report, "Resultado", "SUCESSO DE TRANSPORTE");
            Line(report, "Status HTTP", $"{(int)response.StatusCode} {response.ReasonPhrase}");
            Line(report, "HTTP negociado", response.Version.ToString());
            Line(report, "Tempo até headers", FormatElapsed(headersElapsed));
            Line(report, "Tempo total", FormatElapsed(clock.Elapsed));
            Line(report, "Content-Encoding", string.Join(", ", response.Content.Headers.ContentEncoding));
            Line(report, "Server", string.Join(", ", response.Headers.Server.Select(value => value.ToString())));
            Line(report, "x-request-id", TryHeader(response, "x-request-id"));
            Line(report, "Corpo", SanitizeBody(body));
        }
        catch (Exception ex)
        {
            Line(report, "Resultado", timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested ? "TIMEOUT" : "FALHA");
            Line(report, "Tempo até falha/timeout", FormatElapsed(clock.Elapsed));
            AppendException(report, ex);
        }
        report.Append(certificateReport);
    }

    private static void AppendCertificateValidation(
        StringBuilder report,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        report.AppendLine("Certificado apresentado pelo endpoint:");
        Line(report, "  SslPolicyErrors", errors.ToString());
        if (certificate is null)
        {
            report.AppendLine("  <nenhum certificado>");
            return;
        }

        var certificate2 = certificate as X509Certificate2 ?? new X509Certificate2(certificate);
        AppendCertificate(report, certificate2, "  ");
        if (chain is null)
        {
            report.AppendLine("  Cadeia: indisponível");
            return;
        }

        report.AppendLine($"  Cadeia ({chain.ChainElements.Count} elemento(s)):");
        foreach (var element in chain.ChainElements)
        {
            report.AppendLine($"    Subject: {element.Certificate.Subject}");
            report.AppendLine($"    Issuer: {element.Certificate.Issuer}");
            report.AppendLine($"    Thumbprint: {element.Certificate.Thumbprint}");
            var status = element.ChainElementStatus.Length == 0
                ? "NoError"
                : string.Join(" | ", element.ChainElementStatus.Select(item => $"{item.Status}: {item.StatusInformation.Trim()}"));
            report.AppendLine($"    ChainStatus: {status}");
        }
    }

    private static void AppendCertificate(StringBuilder report, X509Certificate2 certificate, string indent)
    {
        report.AppendLine($"{indent}Subject: {certificate.Subject}");
        report.AppendLine($"{indent}Issuer: {certificate.Issuer}");
        report.AppendLine($"{indent}Serial: {certificate.SerialNumber}");
        report.AppendLine($"{indent}Thumbprint: {certificate.Thumbprint}");
        report.AppendLine($"{indent}Validade: {certificate.NotBefore:O} até {certificate.NotAfter:O}");
        report.AppendLine($"{indent}Assinatura: {certificate.SignatureAlgorithm?.FriendlyName} ({certificate.SignatureAlgorithm?.Value})");
    }

    private static void AppendException(StringBuilder report, Exception exception)
    {
        report.AppendLine("Exception completa:");
        report.AppendLine(Indent(exception.ToString(), "  "));
        report.AppendLine("Cadeia tipada:");
        var depth = 0;
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var prefix = new string(' ', depth * 2 + 2);
            report.AppendLine($"{prefix}[{depth}] Type={current.GetType().FullName}");
            report.AppendLine($"{prefix}Message={current.Message}");
            report.AppendLine($"{prefix}HResult=0x{current.HResult:X8} ({current.HResult})");
            report.AppendLine($"{prefix}StackTrace={current.StackTrace ?? "<indisponível>"}");

            if (current is HttpRequestException http)
            {
                report.AppendLine($"{prefix}HttpRequestException.HttpRequestError={http.HttpRequestError}");
                report.AppendLine($"{prefix}HttpRequestException.StatusCode={http.StatusCode?.ToString() ?? "<sem resposta HTTP>"}");
            }
            if (current is SocketException socket)
            {
                report.AppendLine($"{prefix}SocketException.SocketErrorCode={socket.SocketErrorCode}");
                report.AppendLine($"{prefix}SocketException.NativeErrorCode={socket.NativeErrorCode}");
            }
            if (current is AuthenticationException)
                report.AppendLine($"{prefix}AuthenticationException=true (falha durante autenticação/handshake TLS)");
            if (current is System.ComponentModel.Win32Exception win32)
                report.AppendLine($"{prefix}Win32Exception.NativeErrorCode={win32.NativeErrorCode}");
            if (current is OperationCanceledException canceled)
                report.AppendLine($"{prefix}OperationCanceledException.CancellationToken.IsCancellationRequested={canceled.CancellationToken.IsCancellationRequested}");
            depth++;
        }
    }

    private static async Task<string> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Não foi possível iniciar {fileName}.");
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var stdout = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
            var output = await stdout;
            var error = await stderr;
            var text = string.IsNullOrWhiteSpace(error) ? output : output + Environment.NewLine + error;
            return $"ExitCode={process.ExitCode}{Environment.NewLine}{text}";
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
    }

    private static string TryHeader(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? string.Join(", ", values) : "<ausente>";

    private static string SanitizeBody(string body)
    {
        var compact = (body ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        if (compact.Length > 500) compact = compact[..500] + "...";
        return compact;
    }

    private static string BuildSummary(string report, TimeSpan elapsed)
    {
        var transportSuccesses = CountOccurrences(report, "Resultado: SUCESSO DE TRANSPORTE");
        var timeouts = CountOccurrences(report, "Resultado: TIMEOUT");
        var tlsErrors = CountOccurrences(report, "AuthenticationException=true");
        return $"Diagnóstico concluído em {FormatElapsed(elapsed)}. " +
               $"Probes HTTP com transporte concluído: {transportSuccesses}/7; timeouts: {timeouts}; falhas TLS tipadas: {tlsErrors}.";
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static bool IsInspectionCertificate(X509Certificate2 certificate)
    {
        var text = $"{certificate.Subject} {certificate.Issuer} {certificate.FriendlyName}";
        return new[] { "kaspersky", "zscaler", "fortinet", "fortigate", "palo alto", "sophos", "eset", "endpoint", "websense" }
            .Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSecuritySoftwareName(string name)
        => new[] { "avp", "kaspersky", "kes", "klnagent", "ksde", "endpoint" }
            .Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string RegistryText(object? value)
        => value?.ToString() ?? "<não configurado>";

    private static string SanitizeProxyValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "<vazio>";
        if (value.StartsWith('<') || (value.Contains(',') && !value.Contains("://", StringComparison.Ordinal)))
            return value;
        return string.Join(";", value.Split(';').Select(part =>
        {
            var pieces = part.Split('=', 2);
            if (pieces.Length == 2 && Uri.TryCreate(AddProxyScheme(pieces[1]), UriKind.Absolute, out var namedUri))
                return pieces[0] + "=" + SanitizeUri(namedUri);
            return Uri.TryCreate(AddProxyScheme(part), UriKind.Absolute, out var uri) ? SanitizeUri(uri) : part;
        }));
    }

    private static string AddProxyScheme(string value)
        => value.Contains("://", StringComparison.Ordinal) ? value : "http://" + value;

    private static string SanitizeUri(Uri? uri)
    {
        if (uri is null) return "<nenhum>";
        var builder = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty };
        return builder.Uri.ToString().TrimEnd('/');
    }

    private static string FormatElapsed(TimeSpan elapsed)
        => $"{elapsed.TotalMilliseconds:N0} ms ({elapsed})";

    private static string Indent(string value, string indent)
        => string.Join(Environment.NewLine, (value ?? string.Empty)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => indent + line));

    private static void Header(StringBuilder report, string title)
    {
        report.AppendLine();
        report.AppendLine(new string('=', 80));
        report.AppendLine(title);
        report.AppendLine(new string('=', 80));
    }

    private static void Line(StringBuilder report, string name, string value)
        => report.AppendLine($"{name}: {value}");

    private sealed record HttpProbe(
        string Name,
        bool UseProxy,
        SslProtocols SslProtocols,
        Version HttpVersion,
        HttpVersionPolicy VersionPolicy,
        HttpMethod Method,
        bool SendDummyAuthorization);

    private sealed class NetworkEventRecorder : EventListener
    {
        private readonly Stopwatch _clock;
        private readonly object _gate = new();
        private readonly List<string> _events = [];

        public NetworkEventRecorder(Stopwatch clock)
        {
            _clock = clock;
            foreach (var source in EventSource.GetSources()) EnableIfNetworkSource(source);
        }

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            base.OnEventSourceCreated(eventSource);
            EnableIfNetworkSource(eventSource);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            try
            {
                var values = new List<string>();
                for (var index = 0; index < (eventData.Payload?.Count ?? 0); index++)
                {
                    var name = eventData.PayloadNames is not null && index < eventData.PayloadNames.Count
                        ? eventData.PayloadNames[index]
                        : $"arg{index}";
                    var value = SanitizeEventValue(eventData.Payload![index]);
                    if (eventData.EventSource.Name == "System.Net.Security"
                        && name.Equals("protocol", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var protocol))
                    {
                        value += $" ({(SslProtocols)protocol})";
                    }
                    values.Add($"{name}={value}");
                }

                var line = $"[{_clock.Elapsed.TotalMilliseconds,9:N0} ms] {eventData.EventSource.Name}/{eventData.EventName ?? eventData.EventId.ToString(CultureInfo.InvariantCulture)} " +
                           string.Join(", ", values);
                lock (_gate)
                {
                    if (_events.Count < 1000) _events.Add(line);
                }
            }
            catch
            {
                // O diagnóstico nunca pode falhar por causa do próprio EventListener.
            }
        }

        public string BuildReport()
        {
            lock (_gate)
            {
                return _events.Count == 0
                    ? "<nenhum evento System.Net capturado>"
                    : string.Join(Environment.NewLine, _events);
            }
        }

        private void EnableIfNetworkSource(EventSource source)
        {
            if (source.Name.StartsWith("System.Net", StringComparison.Ordinal))
                EnableEvents(source, EventLevel.Verbose, EventKeywords.All);
        }

        private static string SanitizeEventValue(object? value)
        {
            var text = value?.ToString() ?? "<null>";
            if (text.Length > 600) text = text[..600] + "...";
            return text.Replace("\r", " ").Replace("\n", " ");
        }
    }
}
