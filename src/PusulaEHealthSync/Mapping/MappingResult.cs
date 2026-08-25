using System.Text.Json.Nodes;

namespace PusulaEHealthSync.Mapping;

// Bir Pusula kaydi ya gecerli bir FHIR JSON'a donusur, ya da (Diger cinsiyet, eksik
// zorunlu alan gibi) bilinen bir nedenle atlanir. Boylece cagiran kod API'ye bosuna
// istek atmaz, dogrudan "gonderilemedi" olarak loglar.
public abstract record MappingResult
{
    // Note: basarili mapping'in yine de dikkat cekmesi gereken bir yani varsa (orn.
    // Encounter'da bolum otomatik eslesmeyip fallback koda dustuyse) -- SyncLogEntry.Message'a
    // yansitilir, gonderimi engellemez.
    public sealed record Success(JsonObject Resource, string? Note = null) : MappingResult;
    public sealed record Skipped(string Reason) : MappingResult;
}
