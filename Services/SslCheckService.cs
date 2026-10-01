using System.Collections.Concurrent;
using System.Formats.Asn1;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace VisualMonitor.Web.Services;

public sealed record SslCheckResult(string Status, string Message, DateTime CheckedAtUtc,
    DateTime? ExpiresAtUtc = null, int? DaysRemaining = null, string? Issuer = null, DateTime? ValidFromUtc = null, string? Subject = null,
    string[]? Domains = null, string? Organization = null, string? SerialNumber = null, string? SignatureAlgorithm = null);

public sealed class SslCheckService
{
    private readonly ConcurrentDictionary<string, SslCheckResult> _cache = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly SemaphoreSlim _connections = new(4);
    public SslCheckResult? GetCached(string url) => _cache.GetValueOrDefault(url);

    public async Task<SslCheckResult> CheckAsync(string url, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(url, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(url, out var cached) && DateTime.UtcNow - cached.CheckedAtUtc < TimeSpan.FromMinutes(5))
                return cached;
            await _connections.WaitAsync(ct);
            try { return _cache[url] = await ProbeAsync(url, ct); }
            finally { _connections.Release(); }
        }
        finally { gate.Release(); }
    }

    private static async Task<SslCheckResult> ProbeAsync(string url, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            return new("not_applicable", "Website dùng HTTP, không có kết nối TLS để kiểm tra.", now);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        X509Certificate2? certificate = null;
        SslPolicyErrors errors = SslPolicyErrors.None;
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(uri.IdnHost, uri.Port, timeout.Token);
            using var stream = new SslStream(client.GetStream(), false, (_, remote, _, policyErrors) =>
            {
                certificate?.Dispose();
                certificate = remote is null ? null : new X509Certificate2(remote);
                errors = policyErrors;
                return policyErrors == SslPolicyErrors.None;
            });
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = uri.IdnHost,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, timeout.Token);
            if (certificate is null) return new("unknown", "Máy chủ không cung cấp chứng chỉ.", now);
            return Describe(certificate, errors, now);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return new("unknown", "Không kiểm tra được SSL: kết nối quá 10 giây.", now); }
        catch (AuthenticationException)
        {
            return certificate is null
                ? new("critical", "Không thiết lập được kết nối TLS hợp lệ.", now)
                : Describe(certificate, errors, now, true);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        { return new("unknown", "Không kết nối được máy chủ để kiểm tra SSL.", now); }
        finally { certificate?.Dispose(); }
    }

    private static SslCheckResult Describe(X509Certificate2 certificate, SslPolicyErrors errors, DateTime now, bool handshakeFailed = false)
    {
        var expires = certificate.NotAfter.ToUniversalTime();
        var days = (int)Math.Floor((expires - now).TotalDays);
        var reasons = new List<string>();
        if (expires <= now) reasons.Add("Chứng chỉ đã hết hạn");
        if (certificate.NotBefore.ToUniversalTime() > now) reasons.Add("Chứng chỉ chưa đến ngày hiệu lực");
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)) reasons.Add("Chứng chỉ không khớp tên miền");
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors)) reasons.Add("Chuỗi chứng chỉ không được tin cậy hoặc không hợp lệ");
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)) reasons.Add("Không nhận được chứng chỉ");
        if (handshakeFailed && reasons.Count == 0) reasons.Add("Bắt tay TLS thất bại");
        var status = reasons.Count > 0 ? "critical" : expires - now <= TimeSpan.FromDays(30) ? "warning" : "ok";
        var message = reasons.Count > 0 ? string.Join("; ", reasons) : status == "warning" ? "Chứng chỉ sắp hết hạn (30 ngày hoặc ít hơn)." : "Chứng chỉ hợp lệ.";
        var san = certificate.Extensions.FirstOrDefault(x => x.Oid?.Value == "2.5.29.17");
        var domains = san is null ? new[] { certificate.GetNameInfo(X509NameType.DnsName, false) }
            : new X509SubjectAlternativeNameExtension(san.RawData, san.Critical).EnumerateDnsNames().ToArray();
        return new(status, message, now, expires, days, certificate.Issuer,
            certificate.NotBefore.ToUniversalTime(), certificate.Subject, domains,
            ReadOrganization(certificate.SubjectName), certificate.SerialNumber, certificate.SignatureAlgorithm.FriendlyName);
    }

    private static string? ReadOrganization(X500DistinguishedName name)
    {
        var sequence = new AsnReader(name.RawData, AsnEncodingRules.DER).ReadSequence();
        while (sequence.HasData)
        {
            var set = sequence.ReadSetOf();
            while (set.HasData)
            {
                var attribute = set.ReadSequence();
                if (attribute.ReadObjectIdentifier() == "2.5.4.10")
                    return attribute.ReadCharacterString((UniversalTagNumber)attribute.PeekTag().TagValue);
            }
        }
        return null;
    }
}
