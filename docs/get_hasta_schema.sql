-- hasta.hasta tablosunun kolon yapisini cikarir (veri degil, sadece sema)
SELECT
    c.ORDINAL_POSITION,
    c.COLUMN_NAME,
    c.DATA_TYPE,
    c.CHARACTER_MAXIMUM_LENGTH,
    c.IS_NULLABLE,
    c.COLUMN_DEFAULT
FROM INFORMATION_SCHEMA.COLUMNS c
WHERE c.TABLE_SCHEMA = 'hasta'
  AND c.TABLE_NAME   = 'hasta'
ORDER BY c.ORDINAL_POSITION;

-- Varsa bu tabloya bagli kimlik/telefon/adres gibi alt tablolari da gormek icin:
SELECT TABLE_SCHEMA, TABLE_NAME
FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_SCHEMA = 'hasta'
ORDER BY TABLE_NAME;
