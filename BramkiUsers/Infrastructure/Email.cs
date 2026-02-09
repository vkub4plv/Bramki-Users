using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;

namespace BramkiUsers.Infrastructure;

/// <summary>SMTP configuration.</summary>
public sealed class SmtpOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 25;
    public bool UseTls { get; set; } = false;
    public bool UseDefaultCredentials { get; set; } = false;
    public string From { get; set; } = "";
    public string? Username { get; set; }
    public string? Password { get; set; }
}

/// <summary>Inline image for HTML messages (used for <img src="cid:...">).</summary>
public sealed record InlineImage(string ContentId, byte[] Data, string MediaType = "image/png");

public interface IEmailSender
{
    Task SendAsync(
        string to,
        string subject,
        string htmlBody,
        IEnumerable<string>? cc = null,
        IEnumerable<InlineImage>? inlineImages = null);
}

/// <summary>SMTP-based implementation with UTF-8, TLS, CID inline images.</summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly SmtpOptions _opt;
    private readonly ILogger<SmtpEmailSender> _log;
    public SmtpEmailSender(IOptions<SmtpOptions> opt, ILogger<SmtpEmailSender> log)
        => (_opt, _log) = (opt.Value, log);

    public async Task SendAsync(
        string to, string subject, string htmlBody,
        IEnumerable<string>? cc = null,
        IEnumerable<InlineImage>? inlineImages = null)
    {
        using var client = new SmtpClient(_opt.Host, _opt.Port)
        {
            DeliveryMethod = SmtpDeliveryMethod.Network,
            EnableSsl = _opt.UseTls,
            Timeout = 15000
        };

        if (_opt.UseDefaultCredentials) client.UseDefaultCredentials = true;

        if (!string.IsNullOrEmpty(_opt.Username))
            client.Credentials = new NetworkCredential(_opt.Username, _opt.Password);

        try
        {
            _log.LogInformation(
                "SMTP: sending mail To={To} From={From} Subject={Subject} Host={Host} Port={Port} SSL={SSL}",
                to, _opt.From, subject, _opt.Host, _opt.Port, _opt.UseTls);

            using var msg = new MailMessage(_opt.From, to)
            {
                Subject = subject,
                SubjectEncoding = Encoding.UTF8,
                BodyEncoding = Encoding.UTF8,
                HeadersEncoding = Encoding.UTF8
            };
            if (cc != null) foreach (var c in cc) msg.CC.Add(c);

            var images = inlineImages?.ToList();
            if (images is null || images.Count == 0)
            {
                msg.IsBodyHtml = true;
                msg.Body = htmlBody;
            }
            else
            {
                var htmlView = AlternateView.CreateAlternateViewFromString(
                    htmlBody, Encoding.UTF8, MediaTypeNames.Text.Html);
                foreach (var img in images)
                {
                    var lr = new LinkedResource(new MemoryStream(img.Data), img.MediaType)
                    {
                        ContentId = img.ContentId,
                        TransferEncoding = TransferEncoding.Base64
                    };
                    htmlView.LinkedResources.Add(lr);
                }
                msg.AlternateViews.Add(htmlView);
            }

            await client.SendMailAsync(msg);

            _log.LogInformation(
                "SMTP: mail sent successfully To={To} Subject={Subject}",
                to, subject);
        }
        catch (SmtpException ex)
        {
            _log.LogError(ex,
                "SMTP failed. Host={Host} Port={Port} SSL={SSL} From={From} To={To}",
                _opt.Host, _opt.Port, _opt.UseTls, _opt.From, to);
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Mail send failed (non-SMTP). From={From} To={To}",
                _opt.From, to);
            throw;
        }
    }
}

/// <summary>Branding (global footer + images) pulled from disk via options.</summary>
public sealed class MailBrandingOptions
{
    /// <summary>Path to HTML fragment appended to every mail (relative to ContentRoot by default).</summary>
    public string FooterPath { get; set; } = "wwwroot/mail-footer.html";

    /// <summary>Inline images available globally.</summary>
    public List<BrandingImage> Images { get; set; } = new();
}

public sealed record BrandingImage(string ContentId, string Path, string MediaType = "image/png");

public interface IMailBranding
{
    Task<string> GetFooterHtmlAsync(CancellationToken ct = default);
    Task<IReadOnlyList<InlineImage>> GetInlineImagesAsync(CancellationToken ct = default);
}

/// <summary>Loads footer HTML + images from disk. Supports relative paths.</summary>
public sealed class OptionsMailBranding : IMailBranding
{
    private readonly IOptionsMonitor<MailBrandingOptions> _opt;
    private readonly IWebHostEnvironment _env;

    public OptionsMailBranding(IOptionsMonitor<MailBrandingOptions> opt, IWebHostEnvironment env)
    {
        _opt = opt;
        _env = env;
    }

    public async Task<string> GetFooterHtmlAsync(CancellationToken ct = default)
    {
        var p = ResolvePath(_opt.CurrentValue.FooterPath);
        return File.Exists(p) ? await File.ReadAllTextAsync(p, ct) : string.Empty;
    }

    public async Task<IReadOnlyList<InlineImage>> GetInlineImagesAsync(CancellationToken ct = default)
    {
        var results = new List<InlineImage>();
        foreach (var img in _opt.CurrentValue.Images)
        {
            var p = ResolvePath(img.Path);
            if (!File.Exists(p)) continue;

            var bytes = await File.ReadAllBytesAsync(p, ct);
            results.Add(new InlineImage(img.ContentId, bytes, img.MediaType));
        }
        return results;
    }

    private string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path)) return path;
        if (path.StartsWith("wwwroot", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(_env.ContentRootPath, path);
        return Path.Combine(_env.ContentRootPath, path);
    }
}