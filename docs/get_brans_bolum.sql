-- 1) Brans (uzmanlik) referans tablosunun icerigi -- cok satir olabilir, TOP 50 yeterli
SELECT TOP 50 * FROM Ortak.Brans;

-- 2) Bolum (departman) referans tablosunun icerigi -- cok satir olabilir, TOP 50 yeterli
SELECT TOP 50 * FROM Ortak.Bolum;

-- 3) hasta.protokol.BolumId gercekten Ortak.Bolum'e mi bagli, FK'dan teyit
SELECT
    fk.name AS FK_Adi, tp.name AS Kaynak_Tablo, cp.name AS Kaynak_Kolon,
    tr.name AS Referans_Tablo, cr.name AS Referans_Kolon
FROM sys.foreign_keys fk
INNER JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
INNER JOIN sys.tables tp  ON tp.object_id = fkc.parent_object_id
INNER JOIN sys.columns cp ON cp.object_id = tp.object_id AND cp.column_id = fkc.parent_column_id
INNER JOIN sys.tables tr  ON tr.object_id = fkc.referenced_object_id
INNER JOIN sys.columns cr ON cr.object_id = tr.object_id AND cr.column_id = fkc.referenced_column_id
WHERE tp.name IN ('protokol','Personel')
ORDER BY tp.name, cp.name;

-- 4) IK.Personel'in Brans ile iliskisi FK ile gorunmuyorsa, ara tablo (junction) arama
SELECT TABLE_SCHEMA, TABLE_NAME
FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_NAME LIKE '%PersonelBrans%'
   OR TABLE_NAME LIKE '%DoktorBrans%'
   OR TABLE_NAME LIKE '%PersonelUzmanlik%'
   OR (TABLE_NAME LIKE '%Personel%' AND TABLE_NAME LIKE '%Brans%');

-- 5) hasta.protokol icin gereken diger lookup tablolarinin adaylari (GelisTipi/YatisTipi/ProtokolTipi/PersonelTipi)
SELECT TABLE_SCHEMA, TABLE_NAME
FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_NAME LIKE '%GelisTipi%'
   OR TABLE_NAME LIKE '%YatisTipi%'
   OR TABLE_NAME LIKE '%ProtokolTipi%'
   OR TABLE_NAME LIKE '%PersonelTipi%';
