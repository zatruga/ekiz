using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PusulaEHealthSync.Db;
using PusulaEHealthSync.Export;

namespace PusulaEHealthSync.Web.Pages;

// GONDERILEMEYEN LABORATUVAR TETKIKLERI (2026-10-08, kullanici istegi: "buna raporlar
// kismina ekleyelim, bu liste olarak rapor alinabilir olsun").
//
// NEDEN BU RAPOR VAR: kullanici bir idrar tetkikinin neden gitmedigini sordu. Ekrandaki
// mesaj "Bağlı bir kayıt TRƏS'te artık mevcut değil -- önce o kayıt tekrar gönderilmeli"
// diyordu ve YANLISTI (bkz. SyncLogEntry.ErrorCategory'deki "Eşleştirme eksik" notu).
// Gercek sebep Pusula'daki tanim eksigiydi ve o eksigi gidermesi gereken kisinin
// elinde hicbir liste yoktu -- hangi tetkik, kac kayit, neden.
//
// SALT-OKUNUR: bu sayfa hicbir sey gondermez, hicbir sey degistirmez. Yalnizca Pusula'ya
// sorar ve sonucu gosterir/indirir.
//
// ISTENINCE HESAPLAR: sorgu canli veride ~15 saniye suruyor (ilk yazimda 110 saniyeydi,
// bkz. PusulaRepository.GonderilemeyenLabTetkikleriAsync). Her sayfa acilisinda
// calistirmak yanlis olurdu; Bekleyen Isler'deki ayni kalip.
[Authorize]
public class EksikEslestirmeModel(PusulaRepository repository, ILogger<EksikEslestirmeModel> logger) : PageModel
{
    public List<PusulaRepository.EksikEslestirmeSatiri>? Satirlar { get; private set; }
    public string? Hata { get; private set; }

    [BindProperty(SupportsGet = true)]
    [DataType(DataType.Date)]
    public DateOnly? Baslangic { get; set; }

    [BindProperty(SupportsGet = true)]
    [DataType(DataType.Date)]
    public DateOnly? Bitis { get; set; }

    public DateOnly EtkinBaslangic { get; private set; }
    public DateOnly EtkinBitis { get; private set; }

    // Sebebe gore ozet -- "once hangisini duzeltirsem en cok kazandiririm" sorusunun
    // cevabi. Sira kayit sayisina gore, cunku is yuku orada.
    public List<(string Sebep, int Tetkik, int Sonuc)> Ozet =>
        Satirlar is null ? [] :
        [.. Satirlar.GroupBy(s => s.Sebep)
             .Select(g => (Sebep: g.Key, Tetkik: g.Count(), Sonuc: g.Sum(x => x.SonucSayisi)))
             .OrderByDescending(x => x.Sonuc)];

    public int ToplamSonuc => Satirlar?.Sum(s => s.SonucSayisi) ?? 0;

    public void OnGet() => AralikBelirle();

    public async Task OnPostAsync(CancellationToken ct)
    {
        AralikBelirle();
        try
        {
            Satirlar = await repository.GonderilemeyenLabTetkikleriAsync(
                EtkinBaslangic.ToDateTime(TimeOnly.MinValue),
                EtkinBitis.AddDays(1).ToDateTime(TimeOnly.MinValue), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Eksik eşleştirme raporu hesaplanamadı.");
            Hata = "Pusula'ya bağlanılamadı ya da sorgu tamamlanamadı: " + ex.Message;
        }
    }

    public async Task<IActionResult> OnPostIndirAsync(CancellationToken ct)
    {
        AralikBelirle();
        var liste = await repository.GonderilemeyenLabTetkikleriAsync(
            EtkinBaslangic.ToDateTime(TimeOnly.MinValue),
            EtkinBitis.AddDays(1).ToDateTime(TimeOnly.MinValue), ct);

        // Sayi sutunlari Sayi:true -- Excel'de metin olarak gelirse siralanamaz ve
        // toplam alinamaz, oysa bu listenin tek kullanim amaci siralamak.
        var sutunlar = new List<XlsxWriter.Sutun>
        {
            new("Tetkik", 38),
            new("Sonuç sayısı", 14, Sayi: true),
            new("Protokol sayısı", 15, Sayi: true),
            new("LOINC kodu", 20),
            new("Pusula hizmeti", 40),
            new("Neden gönderilemiyor", 30),
        };

        var satirlar = liste.Select(s => new string?[]
        {
            s.TetkikAdi,
            s.SonucSayisi.ToString(System.Globalization.CultureInfo.InvariantCulture),
            s.ProtokolSayisi.ToString(System.Globalization.CultureInfo.InvariantCulture),
            s.LoincKodu,
            s.HizmetAdi,
            s.Sebep,
        });

        var bayt = XlsxWriter.Olustur("Eksik Eşleştirme", sutunlar, satirlar);
        var ad = $"gonderilemeyen-tetkikler-{EtkinBaslangic:yyyyMMdd}-{EtkinBitis:yyyyMMdd}.xlsx";
        return File(bayt, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ad);
    }

    private void AralikBelirle()
    {
        var bugun = DateOnly.FromDateTime(DateTime.Now);
        EtkinBaslangic = Baslangic ?? bugun.AddDays(-29);
        EtkinBitis = Bitis ?? bugun;
        if (EtkinBitis < EtkinBaslangic) (EtkinBaslangic, EtkinBitis) = (EtkinBitis, EtkinBaslangic);
    }
}
