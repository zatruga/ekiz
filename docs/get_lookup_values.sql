-- Asagidaki her SELECT'i sirayla calistirip sonuclari paylasin.
-- Amac: hasta.hasta tablosundaki xxxId kolonlarinin (sayi) hangi anlama
-- (metin) karsilik geldigini gormek. Hasta verisi degil, sadece kod listesi.

-- 1) Cinsiyet (hasta.hasta.CinsiyetId icin)
SELECT * FROM Pusula.Cinsiyet;

-- 2) "Resmi" cinsiyet kodu -- devlete bildirimlerde kullaniliyor olabilir, cok onemli
SELECT * FROM Skrs.CinsiyetResmi;

-- 3) Kan grubu (hasta.hasta.KanGrubuId icin)
SELECT * FROM Pusula.KanGrubu;

-- 4) Kimlik tipi (hasta.hasta.KimlikTipiId icin -- FIN/Pasaport/MYI/DYI ayrimi burada olabilir)
SELECT * FROM Pusula.KimlikTipi;

-- 5) Medeni hal (hasta.hasta.MedeniHaliId icin)
SELECT * FROM Pusula.MedeniHali;

-- 6) Il listesi (hasta.hasta.IlIdEv icin) -- sadece ilk 30 satir, yapisini gormek yeterli
SELECT TOP 30 * FROM Skrs.IlKodlari;

-- 7) Ilce listesi (hasta.hasta.IlceIdEv icin) -- sadece ilk 30 satir
SELECT TOP 30 * FROM Skrs.IlceKodlari;

-- 8) Uyruk/vatandaslik icin aday tablo -- UyrukId hangi tabloya bagli, netlestirmek icin
SELECT TOP 30 * FROM Skrs.UlkeKodlari;
