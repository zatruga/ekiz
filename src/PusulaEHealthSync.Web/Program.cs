using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using PusulaEHealthSync;
using PusulaEHealthSync.Persistence;
using PusulaEHealthSync.Web.Config;

var builder = WebApplication.CreateBuilder(args);

// KULLANICI ISTEGI (2026-08-22): hastane sunucusuna kurulum -- Web dashboard'un da
// Windows Service olarak (IIS/ANCM'siz, dogrudan Kestrel ile) calisabilmesi icin.
// Worker/Program.cs'teki AddWindowsService yorumundaki ayni NO-OP kurali gecerli --
// konsoldan "dotnet run" ile calistirmayi etkilemez.
builder.Services.AddWindowsService(options => options.ServiceName = "PusulaEHealthSync Web");

builder.Services.Configure<DashboardAuthOptions>(builder.Configuration.GetSection("DashboardAuth"));
builder.Services.Configure<DeployOptions>(builder.Configuration.GetSection("Deploy"));

// Servisle AYNI liste (bkz. ServisKayitlari). Elle gonderim (Gonder/Sil/Tumunu gonder)
// Web'den yapilmaya devam ediyor, bu yuzden senkron servisleri burada da lazim.
builder.Services.AddPusulaSenkron(builder.Configuration);

// OTOMATIK GONDERIM BURADA CALISMIYOR (2026-10-23) -- PusulaSyncWorker Windows servisinde
// (bkz. PusulaEHealthSync/Program.cs). IIS havuzu boşta kapandigi icin dongu burada
// gunlerce olu kaldi. AddPusulaArkaPlanIsleri'ni BURAYA EKLEMEYIN: ayni protokol iki
// surecten ayni anda gonderilir.
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

// Aktivite Akisi'nin aralik ozeti icin (bkz. AktiviteModel): sayfalar arasinda gezinirken
// ust karttaki sayilar sabit kalsin ve on binlerce id Pusula'ya her sayfada yeniden
// sorulmasin diye iki dakikalik onbellek kullaniliyor.
builder.Services.AddMemoryCache();

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Login");
    // Ayarlar (canli ortam/kimlik bilgileri dahil) ve Kullanicilar sadece Admin rolune acik.
    options.Conventions.AuthorizePage("/Ayarlar", "AdminOnly");
    options.Conventions.AuthorizePage("/Kullanicilar", "AdminOnly");
    options.Conventions.AuthorizePage("/Guncelle", "AdminOnly");
    // Servisi yeniden baslatabiliyor -- yalnizca yonetici.
    options.Conventions.AuthorizePage("/ServisDurumu", "AdminOnly");
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
