namespace PusulaEHealthSync.Web.Config;

// Simdilik basit kullanici adi/sifre (bkz. konusma) -- ileride LDAP/AD'ye baglanabilir,
// o zaman bu siniftan bir "IAuthenticator" soyutlamasina gecilir. Simdiden soyutlamaya
// gerek yok, tek kullanici dogrulama yolu var.
public class DashboardAuthOptions
{
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
}
