using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace PusulaEHealthSync.Web;

// ONCE GOSTER, ARKADA TAZELE (2026-10-24, kullanici: "aktivite akisi ve genel bakis cok
// yavas listeleniyor, ekranin acilmasi baya suruyor").
//
// ESKI HAL: iki sayfa da hesabi iki dakika onbellekte tutuyordu (GetOrCreateAsync). Sure
// dolunca sayfayi o an acan kisi hesabin TAMAMINI bekliyordu -- ve bu ekranlar tam da
// birkac dakikada bir bakilan ekranlar, yani bekleyen hemen her zaman biri oluyordu.
//
// YENI HAL, uc durum:
//   - sonuc TAZE (yas < taze)      : dogrudan doner, eskisi gibi.
//   - sonuc BAYAT (taze..enEski)   : HEMEN doner, ayni anda arka planda yeniden hesaplanir;
//                                    bir sonraki acilis taze sonucu gorur.
//   - sonuc YOK ya da cok eski     : hesap beklenir (ilk acilis, ya da uzun sure bakilmamis).
//
// enEski BILEREK KISA (cagiranlar 10 dk veriyor): bu bir izleme ekrani. Yarim saat onceki
// sayilari "su an" gibi gostermek, beklemekten daha kotu.
//
// TEK HESAP: ayni anahtar icin ayni anda yalnizca bir hesap kosar. Iki kisi ayni anda
// soguk sayfayi acarsa ikisi de ayni hesabi bekler -- eskiden ikisi ayri ayri hesapliyordu.
//
// ARKA PLAN HESABI ISTEGIN CancellationToken'ini KULLANMAZ: istek bittiginde (kullanici
// sayfayi aldi) hesap yarida kesilmemeli. Hesabi yapan nesneler tekil servisler (bkz.
// AktiviteModel/GenelBakisModel kuruculari), istek bittikten sonra da gecerliler.
public static class TazeOnbellek
{
    private sealed record Kutu<T>(T Deger, DateTime HesaplandiUtc);

    private static readonly ConcurrentDictionary<string, Task> Suren = new();

    // onbelleklenebilir: false donerse sonuc saklanmaz (ornegin Pusula'ya erisilemediyse
    // eksik bir ozet). O durumda varsa onceki sonuc yerinde kalir.
    public static async Task<T> AlAsync<T>(
        IMemoryCache onbellek, string anahtar, Func<CancellationToken, Task<T>> hesapla,
        TimeSpan taze, TimeSpan enEski, Func<T, bool> onbelleklenebilir, ILogger logger)
    {
        if (onbellek.TryGetValue(anahtar, out Kutu<T>? kutu) && kutu is not null)
        {
            if (DateTime.UtcNow - kutu.HesaplandiUtc < taze) return kutu.Deger;

            // Bayat: hemen don, arkada tazele. Hata sayfayi etkilemez -- bayat sonuc
            // zaten ekranda, bir sonraki acilis yeniden dener.
            _ = Hesapla(onbellek, anahtar, hesapla, enEski, onbelleklenebilir)
                .ContinueWith(t => logger.LogWarning(t.Exception, "Onbellek arka planda tazelenemedi: {Anahtar}", anahtar),
                              TaskContinuationOptions.OnlyOnFaulted);
            return kutu.Deger;
        }

        return await Hesapla(onbellek, anahtar, hesapla, enEski, onbelleklenebilir);
    }

    // ONCE KAYIT, SONRA BASLAT: gorev sozluge EKLENDIKTEN sonra calismaya basliyor. Tersi
    // olsaydi cok hizli biten bir hesap kendini sozlukten, sozluge girmeden ONCE silmeye
    // calisirdi; bitmis gorev orada kalir ve anahtar bir daha hic hesaplanmazdi.
    private static Task<T> Hesapla<T>(
        IMemoryCache onbellek, string anahtar, Func<CancellationToken, Task<T>> hesapla,
        TimeSpan enEski, Func<T, bool> onbelleklenebilir)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mevcut = Suren.GetOrAdd(anahtar, tcs.Task);
        if (mevcut != tcs.Task) return (Task<T>)mevcut;

        _ = Task.Run(async () =>
        {
            try
            {
                var deger = await hesapla(CancellationToken.None);
                if (onbelleklenebilir(deger))
                    onbellek.Set(anahtar, new Kutu<T>(deger, DateTime.UtcNow), enEski);
                tcs.SetResult(deger);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
            finally
            {
                Suren.TryRemove(new KeyValuePair<string, Task>(anahtar, tcs.Task));
            }
        });
        return tcs.Task;
    }
}
