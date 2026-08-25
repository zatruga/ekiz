-- hasta.hasta'daki kod kolonlarina (xxxId) karsilik gelen referans/tanim
-- tablolarini bulmaya calisir. Pusula somaogunda genelde ortak bir "Tanim"
-- veya "TanimliDeger" tablosu olur; bulamazsak FK constraint'lerden gidelim.

SELECT
    fk.name                        AS FK_Adi,
    tp.name                        AS Kaynak_Tablo,
    cp.name                        AS Kaynak_Kolon,
    tr.name                        AS Referans_Tablo,
    cr.name                        AS Referans_Kolon
FROM sys.foreign_keys fk
INNER JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
INNER JOIN sys.tables tp  ON tp.object_id = fkc.parent_object_id
INNER JOIN sys.columns cp ON cp.object_id = tp.object_id AND cp.column_id = fkc.parent_column_id
INNER JOIN sys.tables tr  ON tr.object_id = fkc.referenced_object_id
INNER JOIN sys.columns cr ON cr.object_id = tr.object_id AND cr.column_id = fkc.referenced_column_id
WHERE tp.name = 'hasta'
  AND cp.name IN ('CinsiyetId','KimlikTipiId','MedeniHaliId','KanGrubuId','UyrukId','IlIdEv','IlceIdEv')
ORDER BY cp.name;

-- FK tanimli degilse (Pusula'da sik gorulur), tablo adi tahmini ile arama:
SELECT TABLE_SCHEMA, TABLE_NAME
FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_NAME LIKE '%Tanim%'
   OR TABLE_NAME LIKE '%Kod%'
   OR TABLE_NAME LIKE '%Cinsiyet%'
   OR TABLE_NAME LIKE '%MedeniHal%'
   OR TABLE_NAME LIKE '%KanGrubu%'
   OR TABLE_NAME LIKE '%Uyruk%'
   OR TABLE_NAME LIKE '%KimlikTipi%';
