using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using BramkiUsers.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BramkiUsers.Infrastructure;

public interface IMailTemplateProvider
{
    MailTemplate Get(string key);
    IEnumerable<string> Keys { get; }
}

/// <summary>Single template row.</summary>
public sealed record MailTemplate(
    string Key,
    int DMailsId,                 // primary "To" list from DMails
    string SubjectTemplate,
    string HtmlTemplate,
    int? CcDMailsId = null,       // optional CC list from another DMails row
    bool AppendGlobalFooter = true // allow opt-out if needed
)
{
    public static MailTemplate Create(string key, int dmailsId, string subject, string html, int? ccDmailsId = null, bool appendGlobalFooter = true)
        => new(key, dmailsId, subject, html, ccDmailsId, appendGlobalFooter);
}

/* ---------- Config-driven templates (appsettings.json / MailTemplates.json) ---------- */

public sealed class MailTemplateConfig
{
    public List<MailTemplateConfigItem> Templates { get; set; } = new();
}

public sealed class MailTemplateConfigItem
{
    public string Key { get; set; } = "";
    public int DMailsId { get; set; }
    public string SubjectTemplate { get; set; } = "";
    public string HtmlTemplate { get; set; } = "";     // inline HTML (optional when using HtmlTemplatePath)
    public string? HtmlTemplatePath { get; set; }
    public int? CcDMailsId { get; set; }
    public bool AppendGlobalFooter { get; set; } = true;
}

public sealed class ConfigMailTemplateProvider : IMailTemplateProvider
{
    private readonly IOptionsMonitor<MailTemplateConfig> _monitor;
    private readonly IWebHostEnvironment _env;
    private volatile Dictionary<string, MailTemplate> _map;

    public ConfigMailTemplateProvider(IOptionsMonitor<MailTemplateConfig> monitor, IWebHostEnvironment env)
    {
        _monitor = monitor;
        _env = env;
        _map = Build(_monitor.CurrentValue);
        _monitor.OnChange(cfg => _map = Build(cfg));
    }

    public MailTemplate Get(string key)
        => _map.TryGetValue(key, out var t)
           ? t
           : throw new InvalidOperationException($"Unknown mail template '{key}'.");

    public IEnumerable<string> Keys => _map.Keys;

    private Dictionary<string, MailTemplate> Build(MailTemplateConfig cfg)
    {
        var dict = new Dictionary<string, MailTemplate>(StringComparer.OrdinalIgnoreCase);

        foreach (var i in cfg.Templates)
        {
            if (string.IsNullOrWhiteSpace(i.Key))
                throw new InvalidOperationException("Mail template key cannot be empty.");
            if (dict.ContainsKey(i.Key))
                throw new InvalidOperationException($"Duplicate mail template key '{i.Key}'.");

            // If HtmlTemplatePath is set, read file; else use inline HtmlTemplate
            string html = i.HtmlTemplate;
            if (!string.IsNullOrWhiteSpace(i.HtmlTemplatePath))
            {
                var path = ResolvePath(i.HtmlTemplatePath!);
                if (!File.Exists(path))
                    throw new FileNotFoundException($"HtmlTemplatePath not found for template '{i.Key}': {path}", path);

                html = File.ReadAllText(path); // UTF-8 by default
                if (string.IsNullOrWhiteSpace(html))
                    throw new InvalidOperationException($"HtmlTemplatePath file is empty for template '{i.Key}': {path}");
            }

            dict[i.Key] = new MailTemplate(
                i.Key, i.DMailsId, i.SubjectTemplate, html, i.CcDMailsId, i.AppendGlobalFooter);
        }

        return dict;
    }

    private string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path)) return path;
        return Path.Combine(_env.ContentRootPath, path);
    }
}

/* ----------------------------- Mailer ----------------------------- */

/// <summary>
/// Renders and sends a template e-mail to the current address(es) in DMails.
/// Appends a global branding footer and branding inline images to every e-mail by default.
/// </summary>
public sealed class TemplateMailer
{
    private readonly IDbContextFactory<RaportowanieContext> _dbFactory;
    private readonly IEmailSender _sender;
    private readonly IMailTemplateProvider _templates;
    private readonly IMailBranding _branding;
    private readonly ILogger<TemplateMailer> _log;

    public TemplateMailer(
        IDbContextFactory<RaportowanieContext> dbFactory,
        IEmailSender sender,
        IMailTemplateProvider templates,
        IMailBranding branding,
        ILogger<TemplateMailer> log)
    {
        _dbFactory = dbFactory;
        _sender = sender;
        _templates = templates;
        _branding = branding;
        _log = log;
    }

    /// <summary>
    /// Renders and sends an e-mail for the given template key. The <paramref name="model"/> can be
    /// an anonymous object or a dictionary of tokens. HTML tokens are encoded; subject tokens are plain text.
    /// Supports conditional blocks: {{#if Flag}}...{{else}}...{{/if Flag}} (no nesting).
    /// </summary>
    public async Task SendAsync(string templateKey, object model, CancellationToken ct = default)
    {
        var t = _templates.Get(templateKey);

        _log.LogInformation(
            "TemplateMailer: start TemplateKey={TemplateKey}, DMailsId={DMailsId}, CcDMailsId={CcDMailsId}",
            t.Key, t.DMailsId, t.CcDMailsId);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // To:
        var toRow = await db.DMails.AsNoTracking()
            .FirstOrDefaultAsync(m => m.MailsId == t.DMailsId, ct)
            ?? throw new InvalidOperationException($"DMails row {t.DMailsId} not found for template '{templateKey}'.");
        var toList = SplitAddresses(toRow.MailsEmail);
        if (toList.Count == 0)
            throw new InvalidOperationException($"DMails[{t.DMailsId}] has no email.");

        // CC (optional):
        List<string>? ccList = null;
        if (t.CcDMailsId is int ccId)
        {
            var ccRow = await db.DMails.AsNoTracking()
                .FirstOrDefaultAsync(m => m.MailsId == ccId, ct);
            var addrs = SplitAddresses(ccRow?.MailsEmail);
            if (addrs.Count > 0) ccList = addrs;
        }

        _log.LogDebug(
            "TemplateMailer: resolved recipients for TemplateKey={TemplateKey}: ToCount={ToCount}, CcCount={CcCount}",
            templateKey, toList.Count, ccList?.Count ?? 0);

        // Render
        var dict = ToDict(model);
        var subject = RenderSubject(t.SubjectTemplate, dict);
        var html = RenderHtml(t.HtmlTemplate, dict);


        _log.LogDebug(
            "TemplateMailer: rendered subject for TemplateKey={TemplateKey}: {Subject}",
            templateKey, subject);

        // Append global footer (raw HTML)
        if (t.AppendGlobalFooter)
        {
            var footer = await _branding.GetFooterHtmlAsync(ct);
            if (!string.IsNullOrWhiteSpace(footer))
                html += footer;
        }

        // Attach only the branding images that are actually referenced via cid:
        var brandingImages = await _branding.GetInlineImagesAsync(ct);
        var neededCids = ExtractCidContentIds(html);
        var imagesToAttach = brandingImages
            .Where(i => neededCids.Contains(i.ContentId, StringComparer.OrdinalIgnoreCase))
            .ToList();


        _log.LogDebug(
            "TemplateMailer: inline images for TemplateKey={TemplateKey}: NeededCids={NeededCount}, Attached={AttachedCount}",
            templateKey, neededCids.Count, imagesToAttach.Count);

        foreach (var to in toList)
        {
            try
            {
                await _sender.SendAsync(to, subject, html, ccList, imagesToAttach);
                _log.LogInformation(
                    "TemplateMailer: sent TemplateKey={TemplateKey} to {ToAddress}",
                    templateKey, to);
            }
            catch (Exception ex)
            {
                _log.LogError(
                    ex,
                    "TemplateMailer: send failed for TemplateKey={TemplateKey} to {ToAddress}",
                    templateKey, to);
                throw; // bubble up so the page can toast
            }
        }

        _log.LogInformation(
            "TemplateMailer: completed TemplateKey={TemplateKey}. SentCount={SentCount}",
            templateKey, toList.Count);
    }

    /* ----------------- helpers ----------------- */

    private static List<string> SplitAddresses(string? s) =>
        (s ?? "")
        .Split(new[] { ';', ',', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(a => a.Trim())
        .Where(a => a.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static IReadOnlyDictionary<string, object?> ToDict(object model)
    {
        if (model is IReadOnlyDictionary<string, object?> d) return d;
        var t = model.GetType();
        return t.GetProperties()
            .ToDictionary(p => p.Name, p => p.GetValue(model), StringComparer.OrdinalIgnoreCase);
    }

    // -------- Conditional blocks + token rendering --------

    private static readonly Regex IfBlockRx = new(
        @"\{\{\s*#if\s+([A-Za-z0-9_]+)\s*\}\}([\s\S]*?)\{\{\s*/if\s*\1\s*\}\}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ElseTagRx = new(
        @"\{\{\s*else\s*\}\}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TokenRx = new(
        @"\{\{\s*([A-Za-z0-9_]+)\s*\}\}",
        RegexOptions.Compiled);

    private static string ResolveIfBlocks(string template, IReadOnlyDictionary<string, object?> values)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;
        string s = template;
        while (true)
        {
            var m = IfBlockRx.Match(s);
            if (!m.Success) break;

            var key = m.Groups[1].Value;
            var body = m.Groups[2].Value;

            string truePart = body, falsePart = "";
            var em = ElseTagRx.Match(body);
            if (em.Success)
            {
                truePart = body[..em.Index];
                falsePart = body[(em.Index + em.Length)..];
            }

            var truthy = IsTruthy(values.TryGetValue(key, out var v) ? v : null);
            s = s[..m.Index] + (truthy ? truePart : falsePart) + s[(m.Index + m.Length)..];
        }
        return s;
    }

    // SUBJECT: plain text (no HTML encoding)
    private static string RenderSubject(string template, IReadOnlyDictionary<string, object?> values)
    {
        var s = ResolveIfBlocks(template, values);
        return TokenRx.Replace(s, m =>
        {
            var key = m.Groups[1].Value;
            if (!values.TryGetValue(key, out var v) || v is null) return string.Empty;
            return Convert.ToString(v, CultureInfo.CurrentCulture) ?? string.Empty;
        });
    }

    // HTML BODY: HTML-encode token values
    private static string RenderHtml(string template, IReadOnlyDictionary<string, object?> values)
    {
        var s = ResolveIfBlocks(template, values);
        return TokenRx.Replace(s, m =>
        {
            var key = m.Groups[1].Value;
            if (!values.TryGetValue(key, out var v) || v is null) return string.Empty;
            var raw = Convert.ToString(v, CultureInfo.CurrentCulture) ?? string.Empty;
            return WebUtility.HtmlEncode(raw);
        });
    }

    private static bool IsTruthy(object? v)
    {
        if (v is null) return false;
        if (v is bool b) return b;
        if (v is string s)
        {
            var t = s.Trim();
            if (t.Length == 0) return false;
            if (string.Equals(t, "false", StringComparison.OrdinalIgnoreCase)) return false;
            if (t == "0") return false;
            return true;
        }
        try
        {
            if (v is IConvertible) return Convert.ToDecimal(v, CultureInfo.InvariantCulture) != 0m;
        }
        catch { /* ignore */ }
        return true; // any other non-null object
    }

    private static HashSet<string> ExtractCidContentIds(string html)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(html)) return set;

        var rx = new Regex(@"cid:([A-Za-z0-9._\-@]+)", RegexOptions.IgnoreCase);
        foreach (Match m in rx.Matches(html))
            if (m.Groups.Count > 1 && !string.IsNullOrWhiteSpace(m.Groups[1].Value))
                set.Add(m.Groups[1].Value.Trim());
        return set;
    }
}