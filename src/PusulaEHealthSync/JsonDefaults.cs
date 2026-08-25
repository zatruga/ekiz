using System.Text.Encodings.Web;
using System.Text.Json;

namespace PusulaEHealthSync;

// System.Text.Json'un varsayilan encoder'i ASCII disi karakterleri (Ə, İ, Ş, Ç, Ö, Ü, Ğ...)
// \uXXXX olarak escape ediyor -- teknik olarak BOZUK DEGIL (gecerli JSON, sunucu dogru
// decode eder, 2026-08-20'de DB'deki ham degerle canli gonderilen govde karsilastirilarak
// dogrulandi) ama insan gozüyle okunamaz hale geliyor: "İMAMİR" gorup "karakter
// hatasi var" sanmak kolay. UnsafeRelaxedJsonEscaping sadece JSON'un zorunlu kildigi
// karakterleri (", \, kontrol karakterleri) escape eder, Azerbaycan alfabesi dahil geri
// kalan her seyi oldugu gibi birakir -- hem gonderilen govdede hem dashboard'daki JSON
// panellerinde artik okunabilir.
public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static readonly JsonSerializerOptions Indented = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };
}
