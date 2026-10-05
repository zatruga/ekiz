namespace PusulaEHealthSync.Reporting;

// GUN SONU RAPORU -- bir yerel gunun gonderim bilancosu.
//
// KULLANICI ISTEGI (2026-10-05): "gun sonu ozeti gibi dusun, ornegin bu gun ayin 5, gelen
// mail 4 icin: x kadar hasta gonderilmesi denenildi, y tanesi basarili, z tanesi hatali,
// hatali olanlarin listesi asagidaki gibidir."
//
// "HASTA" DEGIL PROTOKOL: kullanicinin "x kadar hasta" dedigi sey gonderim biriminde
// protokol. Ayni hastanin ayni gun iki protokolu olabilir ve ikisi ayri ayri gonderilir,
// bu yuzden sayim protokol bazli (bkz. GunSonuRaporService).
public record GunSonuRaporu(
    DateOnly Gun,
    string Ortam,
    bool OtomatikAcik,

    // -- PROTOKOL BAZLI (raporun ust satiri) --
    int DenenenProtokol,
    int BasariliProtokol,
    int HataliProtokol,
    int EksikVeriliProtokol,

    // -- KAYIT BAZLI --
    int ToplamGonderim,
    int Basarili,
    int Hatali,
    int Atlanan,
    int Silinen,

    List<GunSonuKaynakSatiri> KaynakKirilimi,
    List<GunSonuHataGrubu> HataGruplari,
    List<GunSonuHataliKalem> HataliKalemler,
    int HataliKalemToplam,

    // -- ILERIYE DONUK (su anki durum, gune ait degil) --
    int SuAnBekleyenProtokol,
    int SuAnTakilanProtokol,

    DateTime? SonTurYerel,
    string? SonTurOzet)
{
    public bool SorunVar => Hatali > 0 || Atlanan > 0;

    // Hic deneme yapilmamis gun: ya otomatik gonderim kapali ya da dongu calismamis.
    // Rapor bu durumu SORUN olarak gostermeli -- "0 hata" ile "hic denenmedi" ayni sey degil.
    public bool HicDenenmedi => ToplamGonderim == 0 && Silinen == 0;

    public int BasariYuzdesi => ToplamGonderim == 0
        ? 0
        : (int)Math.Round(100.0 * Basarili / ToplamGonderim);
}

// Kaynak tipine gore kirilim (Müayinə, Tanı, Tetkik, Epikriz ...).
public record GunSonuKaynakSatiri(string Etiket, int Basarili, int Hatali, int Atlanan)
{
    public int Toplam => Basarili + Hatali + Atlanan;
}

// Hata kategorisine gore kirilim. Baslik/Aciklama SyncLogEntry.ErrorCategory'den gelir --
// kategori listesi tek yerde kalsin diye rapor kendi siniflandirmasini YAPMIYOR.
public record GunSonuHataGrubu(string Baslik, string Aciklama, int Adet, string OrnekSebep);

// Tek bir basarisiz/atlanan kalem. ProtokolId null ise Pusula'da karsiligi cozulemedi
// (hasta ve doktor kayitlari protokole ait degil -- bkz. ProtokolIdleriniCozAsync).
//
// HASTA ADI YOK (KULLANICI KARARI 2026-10-05): "sadece protokol numarasi". Rapor
// e-postayla dolasiyor; kopyalari, yonlendirmeleri ve arsivleri kontrolumuz disinda.
// Protokol numarasi Pusula'da kaydi acmak icin yeterli.
public record GunSonuHataliKalem(
    int? ProtokolId,
    string KaynakAdi,
    int PusulaId,
    string Sebep,
    bool Atlandi,
    DateTime ZamanYerel);
