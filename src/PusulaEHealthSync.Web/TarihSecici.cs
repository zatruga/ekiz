namespace PusulaEHealthSync.Web;

// _TarihSecici partial'inin modeli. Etiket: dugmenin ustundeki kucuk yazi ("Açılış",
// "Taburcu"); bossa yalnizca secili hazir araligin adi yazar. Ipucu: dugmenin title'i.
public record TarihSecici(DateOnly From, DateOnly To, string Etiket = "", string? Ipucu = null);
