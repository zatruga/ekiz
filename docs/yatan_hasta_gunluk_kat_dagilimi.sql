/*
  Gun x Kat bazinda yatan hasta sayimi.

  Analiz.usp_YH_GetDonemYatanHastaListesi prosedurunun FILTRE mantigini temel alir,
  ama prosedurun kendisini EXEC ETMEZ: o prosedur yatis basina TEK satir dondurur,
  bizim ihtiyacimiz ise yatisi gunlere yaymak. Ayrica prosedurun gelir/ilac/ICD
  hesaplari (ufns_* skaler fonksiyonlari) bu rapor icin gereksiz ve cok pahali.

  SADECE SELECT -- Pusula DB salt-okunur.

  DOGRULAMA (2026-09-02, 05.08.2026 / "2. Mertebe 1. Blok"): Pusula ekrani 17 satir
  gosterirken bu sorgu 10 donuyordu. Fark, o gun TABURCU OLAN 7 hastaydi -- Pusula
  taburcu gununu sayiyor, ilk surumdeki varsayilan saymiyordu. Bkz. @TaburcuGunuSayilsin.
*/

DECLARE @Bas DATE = '2026-08-01';
DECLARE @Bit DATE = '2026-08-30';   -- dahil

-- 1 = gunubirlik yatislar dahil, 0 = haric (prosedurdeki @pIsGunubirlikDahil)
DECLARE @GunubirlikDahil BIT = 0;

-- 1 = taburcu gunu de sayilir  -> PUSULA EKRANI ILE AYNI ("o gun serviste bulunmus")
-- 0 = taburcu gunu sayilmaz    -> gece yarisi sayimi ("o gece yatagi isgal etmis")
DECLARE @TaburcuGunuSayilsin BIT = 1;

-- Bos birakilirsa tum subeler. Ornek: '1,3'
DECLARE @SubeIds NVARCHAR(MAX) = NULL;

;WITH Gunler AS (
    SELECT @Bas AS Gun
    UNION ALL
    SELECT DATEADD(DAY, 1, Gun) FROM Gunler WHERE Gun < @Bit
),
Yatislar AS (
    SELECT
        TY.Id AS YatisId,
        TY.HastaId,
        TY.KatId,
        CAST(ISNULL(TY.ServisKabulTarihi, HP.AcilisTarihi) AS DATE) AS GirisGun,
        CAST(TY.TaburcuTarihi AS DATE)                              AS CikisGun
    FROM Tedavi.Yatis TY WITH (NOLOCK)
        INNER JOIN Hasta.Protokol HP WITH (NOLOCK) ON HP.Id = TY.ProtokolId
        INNER JOIN Hasta.Hasta    HH WITH (NOLOCK) ON HH.Id = TY.HastaId
    WHERE TY.State >= 3
      AND HP.GelisTipiId IN ('G', 'Y')
      AND HH.Adi NOT LIKE '%DUMMY%'
      AND (@GunubirlikDahil = 1 OR ISNULL(TY.IsGunubirlik, 0) = 0)
      AND (
            @SubeIds IS NULL OR LTRIM(RTRIM(@SubeIds)) = ''
            OR ISNULL(TY.SubeId, HP.SubeId) IN
               (SELECT * FROM [Sistem].[ufn_ConvertStringToTable](@SubeIds))
          )
      -- donemle hic kesismeyen yatislari en basta ele
      AND ISNULL(TY.ServisKabulTarihi, HP.AcilisTarihi) < DATEADD(DAY, 1, @Bit)
      AND ISNULL(CAST(TY.TaburcuTarihi AS DATE), @Bit) >= @Bas
)
SELECT
    G.Gun,
    ISNULL(BB.Adi, '(Kat belirtilmemis)') AS Kat,
    COUNT(DISTINCT Y.HastaId)             AS YatanHastaSayisi,
    COUNT(*)                              AS YatisSatirSayisi  -- Pusula ekranindaki satir sayisi
FROM Gunler G
    INNER JOIN Yatislar Y
        ON  Y.GirisGun <= G.Gun
        AND (
              Y.CikisGun IS NULL
              OR (@TaburcuGunuSayilsin = 0 AND Y.CikisGun >  G.Gun)
              OR (@TaburcuGunuSayilsin = 1 AND Y.CikisGun >= G.Gun)
            )
    LEFT JOIN Ortak.BinaBirim BB WITH (NOLOCK) ON BB.Id = Y.KatId
GROUP BY G.Gun, BB.Adi
ORDER BY G.Gun, Kat
OPTION (MAXRECURSION 0);
