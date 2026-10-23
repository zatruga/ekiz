using PusulaEHealthSync;

var builder = Host.CreateApplicationBuilder(args);

// KULLANICI ISTEGI (2026-08-22): hastane sunucusuna kurulum -- Windows Service olarak
// baslatilinca (SCM tarafindan) dogru sekilde calisması icin gerekli. Konsoldan
// "dotnet run" ile calistirilinca (mevcut yerel gelistirme deneyimi) bu cagri NO-OP --
// sadece process gercekten bir Windows Service olarak baslatildiginda devreye girer.
builder.Services.AddWindowsService(options => options.ServiceName = "PusulaEHealthSync Worker");

// OTOMATIK GONDERIMIN EVI (2026-10-23).
//
// Gonderim donguleri 2026-09-10'dan beri IIS'teki Web uygulamasinin icindeydi. Sunucu
// gunlugunde olculdu: 12.10'dan beri gonderim yalnizca 1-2 saatlik patlamalar halinde
// calisti, aralarinda 21-68 saat HIC satir yok (17, 18, 20 Ekim bos). Sebep IIS: uygulama
// havuzu 20 dk istek gelmezse kapaniyor ve arka plan dongusu onunla birlikte oluyor;
// biri paneli acana kadar da geri gelmiyor. "Otomatik" gonderim fiilen "panel acikken"
// gonderimdi.
//
// Windows servisi bu baglari tasimiyor: sunucu acilinca baslar, kimse paneli acmasa da
// calisir, cokerse SCM yeniden baslatir. Guncelleyici bu servisi zaten her dagitimda
// durdurup baslatiyordu -- icinde yalnizca tek seferlik bir test araci vardi.
//
// Web artik yalnizca ekran: ayni ayar veritabanini okudugu icin Durdur/Baslat ve canli
// serit oradan calismaya devam ediyor.
builder.Services.AddPusulaSenkron(builder.Configuration);
builder.Services.AddPusulaArkaPlanIsleri();

var host = builder.Build();
host.Run();
