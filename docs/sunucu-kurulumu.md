# Sunucu kurulumu ve güncelleme

Bu dosya **hassas bilgi içermez** -- kimlik bilgileri, şifreler ve bağlantı
dizeleri yalnızca sunucudaki `appsettings.Production.json` dosyalarında durur ve
bu depoya hiçbir zaman girmez.

> Yazılma sebebi: 23.09.2026'da dağıtım yolunu bulmak için IIS yapılandırmasını
> okumak gerekti. Bu bilgi ne depoda ne de başka bir belgede kayıtlıydı.

---

## Uygulama sunucusu

| | |
|---|---|
| Adres | `https://10.10.201.57/` |
| Kök klasör | `F:\Apps\PusulaEHealthSync\` |
| Web sunucusu | IIS -- site fiziksel yolu `F:\Apps\PusulaEHealthSync\Web` |

Pusula veritabanı **ayrı bir makinede**: `10.10.201.67\pusula` (SALT OKUNUR,
bkz. `reference_pusula_sql_diagnostics`).

## Klasör yapısı

```
F:\Apps\PusulaEHealthSync\
├── Web\        IIS sitesi -- panel (Protokol Detay, Eşleştirme sayfaları, Güncelle)
├── Worker\     PusulaSyncWorker Windows servisi -- otomatik gönderim döngüsü
├── Updater\    PusulaEHealthSync.Updater servisi -- güncelleme tetikleyicisini izler
├── Repo\       git deposu (güncelleme buraya pull eder, buradan publish edilir)
├── Staging\    derleme çıktısı -- BAŞARILI olursa Web/ ve Worker/ üzerine kopyalanır
├── Backup\     her güncellemeden önceki sürüm, son 5 tanesi saklanır
├── Control\    tetikleyici ve durum dosyaları (aşağıda)
├── Data\       synclog.db -- senkron günlüğü, ayarlar, eşleştirme tabloları
└── Logs\
```

**Üç ayrı uygulama olduğuna dikkat:** panel (Web), otomatik gönderim servisi
(Worker) ve güncelleyici (Updater). Güncelleme Web ve Worker'ı yeniler; Updater
kendini güncellemez (çalışırken kendi dosyalarını değiştiremeyeceği için).

## `Data\synclog.db` içeriği

Uygulamanın **kendi** veritabanı. Pusula'ya hiçbir şey yazılmadığı için kalıcı
olması gereken her şey burada durur:

| Tablo | İçerik |
|---|---|
| `SyncLog` | Her gönderim denemesi -- istek/yanıt gövdesi dahil |
| `Settings` | Panel ayarları, Pusula ve e-Health bağlantı bilgileri |
| `Users` | Panel kullanıcıları |
| `BolumMapping` | Pusula bölümü → AZ `hospital-departments` kodu |
| `LabTestLoincMapping` | Pusula lab test kodu → LOINC (bkz. `LabTestLoincStore`) |

Son iki tablo, Pusula salt okunur olduğu için eşleştirmelerin nerede tutulduğunun
cevabıdır -- gönderim anında uygulanırlar.

## Güncelleme

### Nasıl tetiklenir

Panelin **Güncelle** sayfasından, ya da doğrudan `Control\` klasörüne dosya
bırakarak (UNC erişimi yeterli, uzak masaüstü gerekmez):

| Dosya | Etki |
|---|---|
| `update.trigger` | Güncellemeyi başlatır |
| `rollback.trigger` | Son yedeğe geri döner |
| `check.trigger` | Yalnızca kontrol eder -- bekleyen commit var mı |

Updater servisi bu klasörü **3 saniyede bir** yoklar.

### Durum dosyaları (okuma)

| Dosya | İçerik |
|---|---|
| `update-status.json` | Son işlemin sonucu, commit hash'i, yedek klasörü |
| `update-check.json` | `DeployedCommitHash`, `PendingCommits`, `HasUpdate` |
| `update-history.json` | Geçmiş güncellemeler |

Sunucunun hangi sürümde olduğunu öğrenmenin en hızlı yolu
`update-check.json` dosyasını okumaktır.

### Güncelleme sırası (`UpdateOrchestrator`)

1. `Repo\` içinde `git fetch` + `reset --hard origin/main`
2. `dotnet publish` → **`Staging\`** (servisler hâlâ çalışıyor, kesinti yok)
3. Derleme başarılıysa: `PusulaSyncWorker` durdurulur, `app_offline.htm` konur
4. Mevcut `Web\` ve `Worker\` → `Backup\<tarih>\` (5'ten eskiler silinir)
5. `Staging\` → `Web\` ve `Worker\` (robocopy)
6. `app_offline.htm` kaldırılır, servis yeniden başlatılır

**Kesinti yalnızca 3–6. adımlar boyunca sürer** (birkaç saniye); indirme ve
derleme süresi kesintiye yansımaz. Derleme başarısız olursa hiçbir şey
değişmez -- çalışan sürüm ayakta kalır.

### Asla üzerine yazılmayan dosya

```
appsettings.Production.json
```

Robocopy bu dosyayı hariç tutar. Bağlantı dizeleri, e-Health kimlik bilgileri ve
dağıtım yolları burada durur; güncelleme onlara dokunmaz.

## Sık gereken kontroller

Hepsi UNC üzerinden, yalnızca okuma:

```bash
B="//10.10.201.57/f$/Apps/PusulaEHealthSync"

# Hangi commit dağıtılmış, bekleyen var mı?
cat "$B/Control/update-check.json"

# Son güncelleme başarılı mıydı?
cat "$B/Control/update-status.json"

# Senkron günlüğünü incelemek için kopyala (canlı dosyayı açma)
cp "$B/Data/synclog.db" /tmp/sunucu-synclog.db
```

## Yeni bir sunucuya kurarken

1. `Repo\` klasörüne depoyu klonla (özel depo -- `RepoUrl` içine token gömülü olmalı)
2. `dotnet publish` ile `Web\` ve `Worker\` klasörlerini oluştur
3. Her üç uygulamanın `appsettings.Production.json` dosyasını yaz:
   - `Pusula:ConnectionString` -- salt okunur kullanıcı
   - `EHealth:BaseUrl` / `UserName` / `Password` / `ProviderId`
   - `SyncLog:DbPath` -- `Data\synclog.db`
   - `Deploy:*` -- yukarıdaki klasör yolları (yalnızca Updater'da)
4. IIS sitesini `Web\` klasörüne yönlendir
5. `PusulaSyncWorker` ve Updater servislerini kur
6. Panelden ilk kullanıcıyı oluştur, Ayarlar sayfasından bağlantıları doğrula

**Otomatik gönderim döngüsü varsayılan olarak KAPALI gelir** -- bilinçli bir
karardır (bkz. `project_auto_send_roadmap`). Ayarlar sayfasından açılır.
