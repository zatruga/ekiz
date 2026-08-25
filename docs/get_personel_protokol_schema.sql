-- ik.personel ve hasta.protokol tablolarinin kolon yapisini cikarir (veri degil, sadece sema)

SELECT
    'ik.personel' AS Tablo,
    c.ORDINAL_POSITION,
    c.COLUMN_NAME,
    c.DATA_TYPE,
    c.CHARACTER_MAXIMUM_LENGTH,
    c.IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS c
WHERE c.TABLE_SCHEMA = 'ik' AND c.TABLE_NAME = 'personel'

UNION ALL

SELECT
    'hasta.protokol' AS Tablo,
    c.ORDINAL_POSITION,
    c.COLUMN_NAME,
    c.DATA_TYPE,
    c.CHARACTER_MAXIMUM_LENGTH,
    c.IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS c
WHERE c.TABLE_SCHEMA = 'hasta' AND c.TABLE_NAME = 'protokol'

ORDER BY Tablo, ORDINAL_POSITION;
