using BramkiUsers;
using BramkiUsers.Components;
using BramkiUsers.Components.Pages.Employees;
using BramkiUsers.Data;
using BramkiUsers.Infrastructure;
using BramkiUsers.Services;
using BramkiUsers.Wcf;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Server.IISIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Security.Principal;
using Karambolo.Extensions.Logging.File;

var builder = WebApplication.CreateBuilder(args);

// ---- File logging ----
builder.Logging.AddFile(o =>
{
    o.RootPath = builder.Environment.ContentRootPath;
    o.TextBuilder = SimpleLogEntryTextBuilder.Default;
});

builder.Logging.Configure(options =>
{
    options.ActivityTrackingOptions = ActivityTrackingOptions.None;
});

// Emails templates from JSON file
builder.Configuration.AddJsonFile("MailTemplates.json", optional: false, reloadOnChange: true);

// Options binding + validation (Mail templates)
builder.Services.AddOptions<MailTemplateConfig>()
    .Bind(builder.Configuration.GetSection("MailTemplates"))
    .Validate(cfg => cfg.Templates.Select(t => t.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() == cfg.Templates.Count,
              "Mail template keys must be unique.")
    .ValidateOnStart();

// Use config-backed provider
builder.Services.AddSingleton<IMailTemplateProvider, ConfigMailTemplateProvider>();

// Keep TemplateMailer
builder.Services.AddScoped<TemplateMailer>();

// SMTP options + validation
builder.Services.AddOptions<SmtpOptions>()
    .Bind(builder.Configuration.GetSection("Smtp"))
    .Validate(o => !string.IsNullOrWhiteSpace(o.Host), "Smtp:Host required")
    .Validate(o => o.Port > 0, "Smtp:Port must be > 0")
    .ValidateOnStart();

// Email sender
builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();

// ----- Global mail branding (footer + CID images) -----
builder.Services.AddOptions<MailBrandingOptions>()
    .Bind(builder.Configuration.GetSection("MailBranding"))
    .ValidateOnStart();
builder.Services.AddSingleton<IMailBranding, OptionsMailBranding>();

// ----- Blazor (Server) -----
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState(); // for <AuthorizeView/AuthorizeRouteView>

// ----- Windows Auth on IIS -----
builder.Services.AddAuthentication(IISDefaults.AuthenticationScheme);

// ----- Authorization: use AD group SIDs via "groupsid" claim -----
builder.Services.AddAuthorization(options =>
{
    const string GroupSid = "http://schemas.microsoft.com/ws/2008/06/identity/claims/groupsid";

    var groups = builder.Configuration.GetSection("Auth:Groups");
    var SID = new
    {
        All = groups["All"]!,
        Admin = groups["Admin"]!,
        HR = groups["HR"]!,
        Karty = groups["Karty"]!,
        Ochrona = groups["Ochrona"]!
    };

    string[] required = ["All", "Admin", "HR", "Karty", "Ochrona"];
    foreach (var k in required)
        if (string.IsNullOrWhiteSpace(groups[k]))
            throw new InvalidOperationException($"Missing Auth:Groups:{k} in configuration.");

    // primitive, used for global gate
    options.AddPolicy("BramkiAll", p => p.RequireClaim(GroupSid, SID.All));
    options.AddPolicy("BramkiAdmin", p => p.RequireClaim(GroupSid, SID.Admin));
    options.AddPolicy("BramkiHR", p => p.RequireClaim(GroupSid, SID.HR));
    options.AddPolicy("BramkiKarty", p => p.RequireClaim(GroupSid, SID.Karty));
    options.AddPolicy("BramkiOchrona", p => p.RequireClaim(GroupSid, SID.Ochrona));

    // composites
    options.AddPolicy("EmployeesAccess", p => p.RequireAssertion(ctx =>
    {
        var g = ctx.User.FindAll(GroupSid).Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return g.Contains(SID.Admin) || g.Contains(SID.HR) || g.Contains(SID.Karty) || g.Contains(SID.Ochrona);
    }));

    options.AddPolicy("HireAccess", p => p.RequireAssertion(ctx => Has(ctx, SID.Admin) || Has(ctx, SID.HR)));
    options.AddPolicy("EditAccess", p => p.RequireAssertion(ctx => Has(ctx, SID.Admin) || Has(ctx, SID.HR)));
    options.AddPolicy("FireAccess", p => p.RequireAssertion(ctx => Has(ctx, SID.Admin) || Has(ctx, SID.HR)));

    // Cards submenu items
    options.AddPolicy("CardsMenuAccess", p => p.RequireAssertion(ctx => Has(ctx, SID.Admin) || Has(ctx, SID.HR) || Has(ctx, SID.Karty) || Has(ctx, SID.Ochrona)));
    options.AddPolicy("CardsIssueAccess", p => p.RequireAssertion(ctx => Has(ctx, SID.Admin) || Has(ctx, SID.Karty)));
    options.AddPolicy("CardsLostAccess", p => p.RequireAssertion(ctx => Has(ctx, SID.Admin) || Has(ctx, SID.HR)));
    options.AddPolicy("CardsReplacementAccess", p => p.RequireAssertion(ctx => Has(ctx, SID.Admin) || Has(ctx, SID.Ochrona)));

    // Groups page = Admin only
    options.AddPolicy("GroupsAccess", p => p.RequireAssertion(ctx => Has(ctx, SID.Admin)));

    // Emails = All EXCEPT Security, Admin always allowed
    options.AddPolicy("EmailsAccess", p => p.RequireAssertion(ctx =>
    {
        var g = ctx.User.FindAll(GroupSid).Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return g.Contains(SID.Admin) || (g.Contains(SID.All) && !g.Contains(SID.Ochrona));
    }));

    static bool Has(AuthorizationHandlerContext ctx, string sid)
        => ctx.User.HasClaim(GroupSid, sid);
});

// ----- EF Core (two SQL Servers) -----
builder.Services.AddPooledDbContextFactory<HRContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("HR"),
        sql => sql.EnableRetryOnFailure()));

builder.Services.AddPooledDbContextFactory<RaportowanieContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("Raportowanie"),
        sql => sql.EnableRetryOnFailure()));

builder.Services.AddScoped<EmployeeState>();

// VISO options + service account credentials from configuration
builder.Services.AddOptions<VisoOptions>()
    .Bind(builder.Configuration.GetSection("Viso"))
    .Validate(o => !string.IsNullOrWhiteSpace(o.BaseUrl), "Viso:BaseUrl required")
    .ValidateOnStart();
builder.Services.Configure<VisoServiceAccountOptions>(builder.Configuration.GetSection("Viso:ServiceAccount"));

// Factories/singletons
builder.Services.AddSingleton<VisoClientFactory>();
builder.Services.AddSingleton<VisoSessionManager>();                 // the singleton token owner
builder.Services.AddSingleton<IVisoSessionProvider>(sp => sp.GetRequiredService<VisoSessionManager>());
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddSingleton<IClaimsTransformation, DevGroupSidClaims>();
}
builder.Services.AddSingleton<IDepartmentLookup, DepartmentLookup>();

// Run the manager as a hosted background service
builder.Services.AddHostedService(sp => sp.GetRequiredService<VisoSessionManager>());

// Facade uses shared token
builder.Services.AddScoped<VisoFacadeShared>();

builder.Services.AddScoped<IToastService, ToastService>();

builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();

builder.Services.AddScoped(typeof(IAudit<>), typeof(Audit<>));

var app = builder.Build();

app.UseRouting();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.UseAuthentication();
app.UseAuthorization();

// Global gate: only Bramki_All
app.MapRazorComponents<App>()
   .AddInteractiveServerRenderMode()
   .RequireAuthorization("BramkiAll");

app.Run();