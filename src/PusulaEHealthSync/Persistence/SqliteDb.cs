using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace PusulaEHealthSync.Persistence;

// TUM SQLite depolarinin (SyncLog, Settings, BolumMapping, LabTestLoinc, HizmetMapping,
// UserAccount) baglanti dizesini TEK yerden uretir ve dosyayi WAL moduna alir.
//
// NEDEN GEREKTI (2026-09-28 incelemesi): ayni synclog.db dosyasini IKI AYRI SURECI
// aciyor -- IIS'teki Web uygulamasi (icinde AutoSyncWorker de var) ve Windows servisi
// olarak kurulan PusulaSyncWorker (bkz. docs/sunucu-kurulumu.md). Veritabani ise
// SQLite'in varsayilan "delete" journal modundaydi.
//
// Bu modda bir YAZAR, veritabaninin tamamini kilitler ve o sirada gelen her OKUYUCU
// beklemek zorunda kalir. Microsoft.Data.Sqlite acilista busy_timeout'u "Default
// Timeout" degerinden (varsayilan 30 sn) ayarladigi icin belirti "hata" degil
// BEKLEME olur: otomatik gonderim turu yazarken acilan bir sayfa 30 saniyeye kadar
// donar, sonra ya acilir ya "database is locked" ile duser. Teshisi zor bir sinif --
// hata gunluge dusmez, kullanici sadece "sistem yavas" der.
//
// WAL (Write-Ahead Logging) bu iliskiyi kaldirir: okuyucular yazari, yazar da
// okuyuculari bloke etmez. Tek yazar kurali devam eder (iki yazar hala sirayla
// girer) ama okuma tarafi tamamen serbest kalir.
//
// journal_mode DOSYAYA yazilir ve kalicidir -- bir kez uygulanmasi yeterli, sonraki
// acilislarda zaten WAL olarak gelir. Yine de her yeni yol icin bir kez calistiriyoruz
// ki yeni kurulumda (bos dosya) da dogru modda baslasin.
public static class SqliteDb
{
    private static readonly ConcurrentDictionary<string, bool> Hazirlanan = new(StringComparer.OrdinalIgnoreCase);

    public static string ConnectionString(string dbPath)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Pooling = true,
            // busy_timeout bu degerden turetilir: kilit bekleyen istek hemen patlamaz,
            // 30 saniye boyunca tekrar dener.
            DefaultTimeout = 30,
        }.ToString();

        Hazirlanan.GetOrAdd(dbPath, _ => { WalUygula(cs); return true; });
        return cs;
    }

    private static void WalUygula(string connectionString)
    {
        try
        {
            using var conn = new SqliteConnection(connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            // synchronous=NORMAL WAL ile birlikte guvenlidir: islem butunlugu korunur,
            // yalnizca isletim sistemi cokmesinde son islemler kaybolabilir. Senkron
            // GUNLUGU icin bu kabul edilebilir -- kayip bir gunluk satiri, kaydin
            // yeniden gonderilmesine yol acar, veri bozulmasina degil.
            cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // WAL uygulanamazsa (ornegin dosya bir ag paylasiminda duruyorsa) eski
            // davranisla devam et -- bu bir iyilestirme, calisma sarti degil.
        }
    }
}
