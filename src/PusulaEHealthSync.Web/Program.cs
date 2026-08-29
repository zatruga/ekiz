using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using PusulaEHealthSync.Config;
using PusulaEHealthSync.Db;
using PusulaEHealthSync.EHealth;
using PusulaEHealthSync.Persistence;
using PusulaEHealthSync.Sync;
using PusulaEHealthSync.Web.Config;

var builder = WebApplication.CreateBuilder(args);

// KULLANICI ISTEGI (2026-08-22): hastane sunucusuna kurulum -- Web dashboard'un da
// Windows Service olarak (IIS/ANCM'siz, dogrudan Kestrel ile) calisabilmesi icin.
// Worker/Program.cs'teki AddWindowsService yorumundaki ayni NO-OP kurali gecerli --
// konsoldan "dotnet run" ile calistirmayi etkilemez.
builder.Services.AddWindowsService(options => options.ServiceName = "PusulaEHealthSync Web");

builder.Services.Configure<PusulaOptions>(builder.Configuration.GetSection("Pusula"));
builder.Services.Configure<EHealthOptions>(builder.Configuration.GetSection("EHealth"));
builder.Services.Configure<SyncLogOptions>(builder.Configuration.GetSection("SyncLog"));
builder.Services.Configure<DashboardAuthOptions>(builder.Configuration.GetSection("DashboardAuth"));
builder.Services.Configure<DeployOptions>(builder.Configuration.GetSection("Deploy"));

builder.Services.AddSingleton<PusulaRepository>();
builder.Services.AddHttpClient<EHealthClient>();
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SyncLogOptions>>().Value;
    return new SyncLogStore(options.DbPath);
});
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SyncLogOptions>>().Value;
    return new SettingsStore(options.DbPath);
});
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SyncLogOptions>>().Value;
    return new BolumMappingStore(options.DbPath);
});
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SyncLogOptions>>().Value;
    return new UserAccountStore(options.DbPath);
});
builder.Services.AddSingleton<PatientSyncService>();
builder.Services.AddSingleton<PractitionerSyncService>();
builder.Services.AddSingleton<ConditionSyncService>();
builder.Services.AddSingleton<ProcedureSyncService>();
builder.Services.AddSingleton<EncounterSyncService>();
builder.Services.AddSingleton<CompositionSyncService>();
builder.Services.AddSingleton<LabResultSyncService>();
builder.Services.AddSingleton<DeleteService>();
builder.Services.AddSingleton<IPasswordHasher<object>, PasswordHasher<object>>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Login";
        options.LogoutPath = "/Logout";
        options.AccessDeniedPath = "/Login";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("AdminOnly", p => p.RequireRole(UserAccountStore.RoleAdmin));

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Login");
    // Ayarlar (canli ortam/kimlik bilgileri dahil) ve Kullanicilar sadece Admin rolune acik.
    options.Conventions.AuthorizePage("/Ayarlar", "AdminOnly");
    options.Conventions.AuthorizePage("/Kullanicilar", "AdminOnly");
    options.Conventions.AuthorizePage("/Guncelle", "AdminOnly");
}).AddMvcOptions(options =>
{
    // Ayarlar sayfasindaki bircok alan (SMTP kullanici adi/sifresi, Canli ortam
    // bilgileri...) bilinerek bos birakilabilir olmali. Nullable olmayan string
    // property'lere MVC'nin otomatik ekledigi ustu kapali [Required] kurali bunu
    // engelliyordu (canli testte MailRecipients disindaki bos alanlar yuzunden
    // "Kaydedildi" hic gorunmedi) -- bu ayarla o davranis kapatiliyor.
    options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
});

var app = builder.Build();

// Tablo bossa (ilk calistirma / upgrade) mevcut DashboardAuth hesabini Admin olarak
// tohumla -- boylece coklu kullanici sistemine gecis mevcut girisi bozmaz.
using (var seedScope = app.Services.CreateScope())
{
    var dashboardAuth = seedScope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<DashboardAuthOptions>>().Value;
    var userStore = seedScope.ServiceProvider.GetRequiredService<UserAccountStore>();
    await userStore.SeedIfEmptyAsync(dashboardAuth.Username, dashboardAuth.PasswordHash);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();

app.Run();
