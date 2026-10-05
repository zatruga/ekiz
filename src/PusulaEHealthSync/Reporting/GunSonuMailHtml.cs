using System.Globalization;
using System.Net;
using System.Text;

namespace PusulaEHealthSync.Reporting;

// GUN SONU RAPORUNUN E-POSTA GOVDESI.
//
// NEDEN BU KADAR ESKI USUL HTML (tablo icinde tablo, inline style, bgcolor):
//
// Outlook masaustu, HTML'i WORD'un render motoruyla ciziyor -- tarayici motoruyla degil.
// Desteklenmeyenler: flexbox, grid, <svg>, harici stylesheet, cogu surumde
// background-image, bircok CSS3 ozelligi. Alicilar mail.mlpcare.com (Exchange) uzerinden
// okuyacagi icin Outlook varsaymak dogru.
//
// Bu yuzden GRAFIKLER TABLO HUCRESIYLE CIZILIYOR: her bar bir <td>, genisligi yuzde,
// rengi bgcolor. Chart.js ya da inline SVG ile cizilen bir grafik alicida BOS BIR KUTU
// olarak gorunur ve biz bunu hicbir zaman goremezdik -- rapor sessizce islevsizlesirdi.
//
// KULLANICI ISTEGI (2026-10-05): "grafiklerle guclendirip susleyebiliriz."
//
// Palet uygulamanin ACIK tema renkleri (site.css) -- e-postada koyu tema guvenilmez,
// istemciler kendi basina renk cevirir, bu yuzden tek tema.
public static class GunSonuMailHtml
{
    private const string Metin = "#0F172A";
    private const string Soluk = "#55617A";
    private const string Cizgi = "#E4E9F0";
    private const string Zemin = "#F5F7FA";
    private const string Kart = "#FFFFFF";
    private const string Birincil = "#2F6FED";
    private const string Basari = "#059669";
    private const string Uyari = "#C2760C";
    private const string Tehlike = "#DC2626";

    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    // SAYI -- binlik ayracli. Adetler, toplamlar, yuzdeler icin.
    private static string N(int n) => n.ToString("N0", Tr);

    // KIMLIK -- binlik ayraci YOK. Protokol numarasi ve Pusula kayit id'si birer sayi
    // DEGIL, birer kimlik: raporu okuyan kisi onu Pusula'nin arama kutusuna yaziyor.
    // "50.865.463" diye yazilsaydi arama hicbir sey bulmazdi ve raporun tek eylemi
    // calismaz olurdu -- sayilar dogru ama kullanilamaz bir rapor.
    private static string Kimlik(int n) => n.ToString(CultureInfo.InvariantCulture);

    public static string Konu(GunSonuRaporu r)
    {
        var gun = r.Gun.ToString("dd.MM.yyyy", Tr);
        var ortam = r.Ortam == "Live" ? "CANLI" : "Test";

        // KONU SATIRI EN ONEMLI BILGIYI TASIYOR: cogu kisi e-postayi acmadan once konuyu
        // okuyor. "Gun sonu raporu" tek basina hicbir sey anlatmiyor.
        if (r.HicDenenmedi)
            return $"[{ortam}] TRƏS gun sonu {gun} -- HIC GONDERIM YAPILMADI";
        if (r.Hatali > 0)
            return $"[{ortam}] TRƏS gun sonu {gun} -- {N(r.HataliProtokol)} protokolde hata";
        if (r.Atlanan > 0)
            return $"[{ortam}] TRƏS gun sonu {gun} -- {N(r.BasariliProtokol)} protokol tamam, {N(r.EksikVeriliProtokol)} eksik veri";
        return $"[{ortam}] TRƏS gun sonu {gun} -- {N(r.BasariliProtokol)} protokol tamam, sorun yok";
    }

    public static string Govde(GunSonuRaporu r)
    {
        var sb = new StringBuilder(32 * 1024);

        sb.Append($@"<!DOCTYPE html>
<html lang=""tr""><head><meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>TRƏS Gün Sonu Raporu</title></head>
<body style=""margin:0;padding:0;background-color:{Zemin};"">
<table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0""
       style=""background-color:{Zemin};padding:24px 12px;"">
<tr><td align=""center"">
<table role=""presentation"" width=""680"" cellpadding=""0"" cellspacing=""0"" border=""0""
       style=""width:680px;max-width:680px;background-color:{Kart};border:1px solid {Cizgi};
              border-radius:10px;font-family:'Segoe UI',Arial,Helvetica,sans-serif;"">");

        Baslik(sb, r);
        Ozet(sb, r);
        if (!r.HicDenenmedi) KaynakGrafigi(sb, r);
        if (r.HataGruplari.Count > 0) HataGrafigi(sb, r);
        if (r.HataliKalemler.Count > 0) HataliTablo(sb, r);
        Durum(sb, r);
        Altlik(sb, r);

        sb.Append(@"</table></td></tr></table></body></html>");
        return sb.ToString();
    }

    // -- BASLIK -------------------------------------------------------------------------
    private static void Baslik(StringBuilder sb, GunSonuRaporu r)
    {
        var canli = r.Ortam == "Live";
        var seritRengi = r.HicDenenmedi ? Tehlike : r.Hatali > 0 ? Uyari : Basari;

        sb.Append($@"
<tr><td style=""height:4px;background-color:{seritRengi};border-radius:10px 10px 0 0;""></td></tr>
<tr><td style=""padding:24px 28px 8px 28px;"">
  <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0""><tr>
    <td style=""font-size:11px;letter-spacing:1.2px;text-transform:uppercase;color:{Soluk};
               font-weight:600;"">TRƏS Gün Sonu Raporu</td>
    <td align=""right"">
      <span style=""display:inline-block;padding:3px 10px;border-radius:12px;font-size:11px;
                   font-weight:700;letter-spacing:0.5px;
                   background-color:{(canli ? "#FEF2F2" : "#F1F5F9")};
                   color:{(canli ? Tehlike : Soluk)};"">{(canli ? "CANLI ORTAM" : "TEST / SANDBOX")}</span>
    </td>
  </tr></table>
  <div style=""font-size:26px;font-weight:700;color:{Metin};padding-top:6px;"">
    {E(r.Gun.ToString("d MMMM yyyy, dddd", Tr))}
  </div>
  <div style=""font-size:13px;color:{Soluk};padding-top:4px;"">
    Bu rapor {E(r.Gun.ToString("dd.MM.yyyy", Tr))} günü Pusula'dan TRƏS'e yapılan
    gönderimleri kapsar.
  </div>
</td></tr>");
    }

    // -- OZET: KULLANICININ ISTEDIGI CUMLE ----------------------------------------------
    // "x kadar hasta gonderilmesi denenildi, y tanesi basarili, z tanesi hatali"
    private static void Ozet(StringBuilder sb, GunSonuRaporu r)
    {
        if (r.HicDenenmedi)
        {
            var sebep = r.OtomatikAcik
                ? "Otomatik gönderim <strong>açık</strong> görünüyor ama gün içinde tek bir "
                  + "gönderim kaydı yok. Döngü çalışmamış olabilir &mdash; sunucudaki "
                  + "uygulamanın ayakta olduğunu kontrol edin."
                : "Otomatik gönderim <strong>kapalı</strong>. Gönderim yapılması bekleniyorsa "
                  + "Ayarlar &rarr; Otomatik Gönderim bölümünden başlatın.";

            sb.Append($@"
<tr><td style=""padding:4px 28px 20px 28px;"">
  <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0""
         style=""background-color:#FEF2F2;border:1px solid #FECACA;border-radius:8px;"">
  <tr><td style=""padding:16px 18px;font-size:14px;color:{Metin};line-height:1.6;"">
    <strong style=""color:{Tehlike};"">Bu gün hiç gönderim yapılmadı.</strong><br>{sebep}
  </td></tr></table>
</td></tr>");
            return;
        }

        sb.Append($@"
<tr><td style=""padding:4px 28px 0 28px;"">
  <div style=""font-size:15px;color:{Metin};line-height:1.7;padding-bottom:16px;"">
    <strong>{N(r.DenenenProtokol)}</strong> protokol için gönderim denendi &mdash;
    <strong style=""color:{Basari};"">{N(r.BasariliProtokol)}</strong> tanesi sorunsuz
    tamamlandı, <strong style=""color:{(r.HataliProtokol > 0 ? Tehlike : Soluk)};"">{N(r.HataliProtokol)}</strong>
    tanesinde hata alındı{(r.EksikVeriliProtokol > 0
        ? $", <strong style=\"color:{Uyari};\">{N(r.EksikVeriliProtokol)}</strong> tanesi eksik veri yüzünden gönderilemedi"
        : "")}.
  </div>
</td></tr>");

        // KPI kutulari -- 4 hucreli tek satir tablo (flexbox Outlook'ta yok).
        sb.Append($@"
<tr><td style=""padding:0 28px 8px 28px;"">
  <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0""><tr>");

        Kpi(sb, "Gönderim denemesi", N(r.ToplamGonderim), Metin, "kayıt bazlı");
        Kpi(sb, "Başarılı", N(r.Basarili), Basari, $"%{r.BasariYuzdesi}");
        Kpi(sb, "Hatalı", N(r.Hatali), r.Hatali > 0 ? Tehlike : Soluk, "yeniden denenecek");
        Kpi(sb, "Eksik veri", N(r.Atlanan), r.Atlanan > 0 ? Uyari : Soluk, "düzeltme gerekiyor");

        sb.Append("</tr></table></td></tr>");

        if (r.Silinen > 0)
            sb.Append($@"
<tr><td style=""padding:0 28px 8px 28px;"">
  <div style=""font-size:12px;color:{Soluk};"">
    Ayrıca iptal senkronu <strong>{N(r.Silinen)}</strong> kaydı TRƏS'ten sildi
    (Pusula'da iptal edilmiş ya da silinmiş kayıtlar).
  </div>
</td></tr>");
    }

    private static void Kpi(StringBuilder sb, string etiket, string deger, string renk, string alt)
    {
        sb.Append($@"
    <td width=""25%"" valign=""top"" style=""padding:0 4px;"">
      <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0""
             style=""background-color:{Zemin};border:1px solid {Cizgi};border-radius:8px;"">
      <tr><td style=""padding:12px 10px;text-align:center;"">
        <div style=""font-size:22px;font-weight:700;color:{renk};line-height:1.1;"">{deger}</div>
        <div style=""font-size:11px;color:{Soluk};padding-top:4px;font-weight:600;"">{E(etiket)}</div>
        <div style=""font-size:10px;color:#8B95A8;padding-top:2px;"">{E(alt)}</div>
      </td></tr></table>
    </td>");
    }

    // -- GRAFIK 1: KAYNAK TIPINE GORE YIGILI BAR ----------------------------------------
    // Her bar, icinde uc hucreli bir tablo: basarili / hatali / atlanan. Hucre genislikleri
    // yuzde oldugu icin oranlar e-posta istemcisinin verdigi genislige gore olceklenir --
    // sabit piksel verseydik telefonda tasardi.
    private static void KaynakGrafigi(StringBuilder sb, GunSonuRaporu r)
    {
        sb.Append($@"
<tr><td style=""padding:20px 28px 4px 28px;"">
  <div style=""font-size:13px;font-weight:700;color:{Metin};padding-bottom:3px;"">
    Kaynak türüne göre dağılım</div>
  <div style=""font-size:11px;color:{Soluk};padding-bottom:12px;"">
    Her çubuk o türde yapılan denemelerin tamamı; renkler sonucu gösterir.</div>
  <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0"">");

        var enBuyuk = Math.Max(1, r.KaynakKirilimi.Max(k => k.Toplam));

        foreach (var k in r.KaynakKirilimi)
        {
            // Barin TOPLAM genisligi en kalabalik tura gore olceklenir (bos kalan yer gri
            // degil, hic cizilmiyor) -- boylece hangi turde cok is oldugu da gorunur.
            var barYuzde = Math.Max(2, (int)Math.Round(100.0 * k.Toplam / enBuyuk));
            var bRatio = k.Toplam == 0 ? 0 : (int)Math.Round(100.0 * k.Basarili / k.Toplam);
            var hRatio = k.Toplam == 0 ? 0 : (int)Math.Round(100.0 * k.Hatali / k.Toplam);
            var aRatio = Math.Max(0, 100 - bRatio - hRatio);

            sb.Append($@"
    <tr><td style=""padding:0 0 9px 0;"">
      <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0""><tr>
        <td width=""130"" style=""font-size:12px;color:{Metin};padding-right:10px;
                                 white-space:nowrap;"">{E(k.Etiket)}</td>
        <td>
          <table role=""presentation"" width=""{barYuzde}%"" cellpadding=""0"" cellspacing=""0""
                 border=""0"" style=""height:16px;""><tr>");

            if (bRatio > 0) sb.Append($@"<td width=""{bRatio}%"" bgcolor=""{Basari}"" style=""background-color:{Basari};height:16px;font-size:1px;line-height:16px;"">&nbsp;</td>");
            if (hRatio > 0) sb.Append($@"<td width=""{hRatio}%"" bgcolor=""{Tehlike}"" style=""background-color:{Tehlike};height:16px;font-size:1px;line-height:16px;"">&nbsp;</td>");
            if (aRatio > 0 && k.Atlanan > 0) sb.Append($@"<td width=""{aRatio}%"" bgcolor=""{Uyari}"" style=""background-color:{Uyari};height:16px;font-size:1px;line-height:16px;"">&nbsp;</td>");

            sb.Append($@"</tr></table>
        </td>
        <td width=""92"" align=""right"" style=""font-size:11px;color:{Soluk};padding-left:10px;
                                              white-space:nowrap;"">{N(k.Toplam)} deneme</td>
      </tr></table>
    </td></tr>");
        }

        sb.Append($@"</table>
  <div style=""font-size:11px;color:{Soluk};padding-top:6px;"">
    <span style=""color:{Basari};"">&#9632;</span> Başarılı&nbsp;&nbsp;
    <span style=""color:{Tehlike};"">&#9632;</span> Hatalı&nbsp;&nbsp;
    <span style=""color:{Uyari};"">&#9632;</span> Eksik veri
  </div>
</td></tr>");
    }

    // -- GRAFIK 2: HATA KATEGORILERI ----------------------------------------------------
    private static void HataGrafigi(StringBuilder sb, GunSonuRaporu r)
    {
        var enBuyuk = Math.Max(1, r.HataGruplari.Max(h => h.Adet));

        sb.Append($@"
<tr><td style=""padding:20px 28px 4px 28px;"">
  <div style=""font-size:13px;font-weight:700;color:{Metin};padding-bottom:3px;"">
    Hata sebepleri</div>
  <div style=""font-size:11px;color:{Soluk};padding-bottom:12px;"">
    En sık görülenden başlayarak. Aynı sebep çok kayıtta tekrarlıyorsa kök neden tektir.</div>
  <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0"">");

        foreach (var h in r.HataGruplari)
        {
            var yuzde = Math.Max(2, (int)Math.Round(100.0 * h.Adet / enBuyuk));
            sb.Append($@"
    <tr><td style=""padding:0 0 11px 0;"">
      <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0""><tr>
        <td style=""font-size:12px;font-weight:600;color:{Metin};"">{E(h.Baslik)}</td>
        <td align=""right"" style=""font-size:12px;font-weight:700;color:{Tehlike};"">{N(h.Adet)}</td>
      </tr></table>
      <table role=""presentation"" width=""{yuzde}%"" cellpadding=""0"" cellspacing=""0"" border=""0""
             style=""height:10px;margin-top:3px;""><tr>
        <td bgcolor=""{Tehlike}"" style=""background-color:{Tehlike};height:10px;font-size:1px;
                                        line-height:10px;border-radius:2px;"">&nbsp;</td>
      </tr></table>
      <div style=""font-size:11px;color:{Soluk};padding-top:4px;line-height:1.5;"">
        {E(h.Aciklama)}</div>
    </td></tr>");
        }

        sb.Append("</table></td></tr>");
    }

    // -- HATALI KALEM TABLOSU -----------------------------------------------------------
    private static void HataliTablo(StringBuilder sb, GunSonuRaporu r)
    {
        sb.Append($@"
<tr><td style=""padding:20px 28px 4px 28px;"">
  <div style=""font-size:13px;font-weight:700;color:{Metin};padding-bottom:3px;"">
    Gönderilemeyen kayıtlar</div>
  <div style=""font-size:11px;color:{Soluk};padding-bottom:10px;"">
    Protokol numarasını Pusula'da açıp eksiği giderdiğinizde kayıt kendiliğinden gönderilir;
    elle bir şey yapmanız gerekmez.
    {(r.HataliKalemToplam > r.HataliKalemler.Count
        ? $"<br><strong>Toplam {N(r.HataliKalemToplam)} kayıt var, ilk {N(r.HataliKalemler.Count)} tanesi listelendi.</strong>"
        : "")}
  </div>
  <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0""
         style=""border-collapse:collapse;border:1px solid {Cizgi};"">
    <tr bgcolor=""{Zemin}"">
      <th align=""left"" style=""padding:8px 10px;font-size:11px;color:{Soluk};
                               border-bottom:1px solid {Cizgi};width:92px;"">PROTOKOL</th>
      <th align=""left"" style=""padding:8px 10px;font-size:11px;color:{Soluk};
                               border-bottom:1px solid {Cizgi};width:110px;"">KAYIT</th>
      <th align=""left"" style=""padding:8px 10px;font-size:11px;color:{Soluk};
                               border-bottom:1px solid {Cizgi};"">SEBEP</th>
    </tr>");

        var tek = false;
        foreach (var k in r.HataliKalemler)
        {
            tek = !tek;
            var zemin = tek ? Kart : "#FBFCFE";
            var durumRengi = k.Atlandi ? Uyari : Tehlike;
            var protokol = k.ProtokolId is { } p
                ? Kimlik(p)
                : $"<span style=\"color:{Soluk};\">&mdash;</span>";

            sb.Append($@"
    <tr bgcolor=""{zemin}"">
      <td valign=""top"" style=""padding:8px 10px;font-size:12px;color:{Metin};
                               border-bottom:1px solid {Cizgi};font-family:Consolas,monospace;"">{protokol}</td>
      <td valign=""top"" style=""padding:8px 10px;font-size:11px;border-bottom:1px solid {Cizgi};"">
        <span style=""color:{Metin};"">{E(k.KaynakAdi)}</span><br>
        <span style=""color:{durumRengi};font-size:10px;font-weight:600;"">{(k.Atlandi ? "EKSİK VERİ" : "HATA")}</span>
        <span style=""color:#8B95A8;font-size:10px;"">&nbsp;#{Kimlik(k.PusulaId)}</span>
      </td>
      <td valign=""top"" style=""padding:8px 10px;font-size:11px;color:{Soluk};
                               border-bottom:1px solid {Cizgi};line-height:1.5;"">{E(k.Sebep)}</td>
    </tr>");
        }

        sb.Append("</table></td></tr>");
    }

    // -- SU ANKI DURUM ------------------------------------------------------------------
    private static void Durum(StringBuilder sb, GunSonuRaporu r)
    {
        sb.Append($@"
<tr><td style=""padding:20px 28px 4px 28px;"">
  <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0""
         style=""background-color:{Zemin};border:1px solid {Cizgi};border-radius:8px;"">
  <tr><td style=""padding:14px 16px;font-size:12px;color:{Metin};line-height:1.8;"">
    <strong style=""font-size:12px;"">Şu anki durum</strong><br>
    Gönderim bekleyen: <strong>{N(r.SuAnBekleyenProtokol)}</strong> protokol
    &nbsp;&middot;&nbsp;
    Eksik veri yüzünden bekleyen: <strong>{N(r.SuAnTakilanProtokol)}</strong> protokol<br>
    Otomatik gönderim:
    <strong style=""color:{(r.OtomatikAcik ? Basari : Tehlike)};"">
      {(r.OtomatikAcik ? "çalışıyor" : "durduruldu")}</strong>
    {(r.SonTurYerel is { } t
        ? $"&nbsp;&middot;&nbsp; Son tur: <strong>{E(t.ToString("dd.MM.yyyy HH:mm", Tr))}</strong>"
        : "")}
    {(r.SonTurOzet is { } o ? $"<br><span style=\"color:{Soluk};\">{E(o)}</span>" : "")}
  </td></tr></table>
</td></tr>");
    }

    private static void Altlik(StringBuilder sb, GunSonuRaporu r)
    {
        sb.Append($@"
<tr><td style=""padding:18px 28px 24px 28px;border-top:1px solid {Cizgi};"">
  <div style=""font-size:11px;color:#8B95A8;line-height:1.6;"">
    Bu rapor Pusula &ndash; TRƏS entegrasyon uygulaması tarafından otomatik hazırlandı.
    Alıcı listesi Ayarlar &rarr; Gün Sonu Maili bölümünden düzenlenir.<br>
    Hasta adı ve FİN bilgisi bilinçli olarak yer almaz; kaydı protokol numarasıyla
    Pusula'dan açabilirsiniz.
  </div>
</td></tr>");
    }
}
