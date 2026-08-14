SELECT * FROM t_master ORDER BY id
SELECT * FROM t_detail ORDER BY id
SELECT id, name, cat FROM t_master WHERE cat = 'A' ORDER BY id
SELECT id, qty, price FROM t_detail WHERE qty > 2 ORDER BY id
SELECT id, qty, price FROM t_detail WHERE qty BETWEEN 2 AND 10 ORDER BY id
SELECT id, code FROM t_detail WHERE code IN ('a01', 'b02', 'zzz') ORDER BY id
SELECT id, name FROM t_master WHERE name LIKE 'Al*' ORDER BY id
SELECT id, note FROM t_detail WHERE IsNull(note) ORDER BY id
SELECT id, note FROM t_detail WHERE note IS NOT NULL ORDER BY id
SELECT DISTINCT master_id FROM t_detail ORDER BY master_id
SELECT DISTINCTROW master_id FROM t_detail ORDER BY master_id
SELECT TOP 5 id, qty FROM t_detail WHERE qty IS NOT NULL ORDER BY qty DESC, id
SELECT master_id, Count(*) AS cnt, Sum(qty) AS tot_qty, Avg(price) AS avg_price, Min(price) AS min_price, Max(price) AS max_price FROM t_detail GROUP BY master_id ORDER BY master_id
SELECT master_id, Count(*) AS cnt FROM t_detail GROUP BY master_id HAVING Count(*) > 1 ORDER BY master_id
SELECT m.id, m.name, d.id AS did, d.qty FROM t_master m INNER JOIN t_detail d ON m.id = d.master_id ORDER BY d.id
SELECT m.id, m.name, d.qty FROM t_master m LEFT JOIN t_detail d ON m.id = d.master_id ORDER BY m.id, d.id
SELECT m.id, m.name, d.qty FROM t_master m RIGHT JOIN t_detail d ON m.id = d.master_id ORDER BY d.id
SELECT m.id, m.name FROM t_master m WHERE EXISTS (SELECT 1 FROM t_detail d WHERE d.master_id = m.id AND d.qty > 50) ORDER BY m.id
SELECT id, qty FROM t_detail WHERE master_id IN (SELECT id FROM t_master WHERE active = true) ORDER BY id
SELECT m.id, m.name, d.qty FROM t_master m, t_detail d WHERE m.id = d.master_id ORDER BY d.id
SELECT m.id, (SELECT Count(*) FROM t_detail d WHERE d.master_id = m.id) AS n FROM t_master m ORDER BY m.id
SELECT cat, Count(*) AS cnt FROM t_master GROUP BY cat ORDER BY cat
SELECT id, UCase(name), LCase(name), Len(name), Left(name, 3), Right(name, 4), Mid(name, 2, 3) FROM t_master WHERE id = 1
SELECT id, InStr(name, 'ph') AS ipos, Replace(name, 'A', 'X') AS rep, Trim(name) AS tr FROM t_master WHERE id = 1
SELECT id, Abs(qty) AS a, Sgn(qty) AS s, Int(qty) AS i, Fix(qty) AS f FROM t_detail WHERE id = 4
SELECT id, Format(dt, 'yyyy-mm-dd') AS fmt, Year(dt), Month(dt), Day(dt), DatePart('yyyy', dt) FROM t_detail WHERE id = 1
SELECT id, DateAdd('d', 5, dt) AS plus5, DateDiff('d', dt, #1/1/2022#) AS diff_days FROM t_detail WHERE id = 1
SELECT id, CStr(id) AS sid FROM t_master ORDER BY id
SELECT id, name & '!' AS ex FROM t_master WHERE id = 1
SELECT id, qty, price FROM t_detail WHERE qty > 0 AND (price < 0 OR price > 100) ORDER BY id
SELECT m.id, m.name, Count(d.id) AS n FROM t_master m LEFT JOIN t_detail d ON m.id = d.master_id GROUP BY m.id, m.name ORDER BY m.id
SELECT id FROM t_master WHERE cat = 'A' UNION SELECT id FROM t_master WHERE cat = 'B' ORDER BY id
SELECT IIf(qty > 0, 'pos', IIf(qty < 0, 'neg', 'zero')) AS sign_of FROM t_detail WHERE id <= 5 ORDER BY id
SELECT Switch(qty > 0, 'pos', qty < 0, 'neg', true, 'zero') AS sgn_of FROM t_detail WHERE id <= 5 ORDER BY id
SELECT Count(*) AS total, Sum(price) AS total_price FROM t_detail WHERE master_id = 1
SELECT id, price FROM t_detail WHERE price = 10.50 ORDER BY id
SELECT code, Count(DISTINCT master_id) AS n FROM t_detail GROUP BY code ORDER BY code
SELECT id, active FROM t_master WHERE active = true ORDER BY id
SELECT id, created FROM t_master WHERE created < #1/1/2021# ORDER BY id
SELECT id, budget FROM t_master WHERE budget > 0 ORDER BY id
SELECT m.id, m.budget FROM t_master m WHERE m.budget = 1000.00 ORDER BY m.id
SELECT Atn(1) FROM t_detail WHERE id = 1
SELECT Round(2.5, 0) FROM t_detail WHERE id = 1
SELECT Round(-2.5, 0) FROM t_detail WHERE id = 1
SELECT Round(1.005, 2) FROM t_detail WHERE id = 1
SELECT Partition(100, 0, 500, 100) FROM t_detail WHERE id = 1
SELECT DCount('*', 't_detail') FROM t_detail WHERE id = 1
SELECT DCount('qty', 't_detail', 'master_id = 1') FROM t_detail WHERE id = 1
SELECT DSum('qty', 't_detail', 'master_id = 1') FROM t_detail WHERE id = 1
SELECT DLookup('name', 't_master', 'id = 3') FROM t_detail WHERE id = 1
SELECT DMax('qty', 't_detail', 'master_id = 2') FROM t_detail WHERE id = 1
SELECT DMin('price', 't_detail', 'master_id = 2') FROM t_detail WHERE id = 1
SELECT DAvg('price', 't_detail', 'master_id = 1') FROM t_detail WHERE id = 1
SELECT DFirst('code', 't_detail', 'master_id = 1') FROM t_detail WHERE id = 1
SELECT DLast('code', 't_detail', 'master_id = 1') FROM t_detail WHERE id = 1
SELECT DMax('dt', 't_detail') FROM t_detail WHERE id = 1
SELECT Str(5) FROM t_detail WHERE id = 1
SELECT Str(-5) FROM t_detail WHERE id = 1
SELECT Str(1234.5678) FROM t_detail WHERE id = 1
SELECT id & '-' & price FROM t_detail WHERE id = 1
SELECT name & '-' & budget FROM t_master WHERE id = 1
SELECT master_id, Avg(qty) AS avg_qty FROM t_detail GROUP BY master_id ORDER BY master_id
SELECT id, qty FROM t_detail ORDER BY qty DESC, id
WITH big AS (SELECT * FROM t_detail WHERE qty > 5) SELECT count(*) AS n FROM big
SELECT m.id, m.name, d.qty FROM t_master m FULL OUTER JOIN t_detail d ON m.id = d.master_id ORDER BY m.id, d.id
SELECT sub.id, sub.qty FROM (SELECT id, qty FROM t_detail WHERE qty IS NOT NULL) sub ORDER BY sub.id
SELECT Format(dt, 'mmmm dd, yyyy') FROM t_detail WHERE id = 1
SELECT Format(dt, 'mmm yyyy') FROM t_detail WHERE id = 1
SELECT Format(dt, 'ddd') FROM t_detail WHERE id = 1
SELECT Format(dt, 'dddd') FROM t_detail WHERE id = 1
SELECT Format(dt, 'hh:mm:ss') FROM t_detail WHERE id = 1
SELECT Format(1234.5, '#,##0.00') FROM t_detail WHERE id = 1
SELECT Format(1234.5, '0.00') FROM t_detail WHERE id = 1
SELECT Format(0.5, '0%') FROM t_detail WHERE id = 1
SELECT Format(1234.5, '$#,##0.00') FROM t_detail WHERE id = 1
SELECT Format(1234.5, 'general number') FROM t_detail WHERE id = 1
SELECT Format(1234.5, 'fixed') FROM t_detail WHERE id = 1
SELECT Format(1234.5, 'standard') FROM t_detail WHERE id = 1
SELECT Format(1234.5, 'currency') FROM t_detail WHERE id = 1
SELECT Format(0.5, 'percent') FROM t_detail WHERE id = 1
SELECT count(*) FROM t_master WHERE name = 'alpha'
SELECT count(*) FROM t_master WHERE name > 'ALPHA'
SELECT id, code FROM t_detail WHERE code = 'A01' ORDER BY id
SELECT id, note FROM t_detail WHERE note = 'FIRST ITEM' ORDER BY id
SELECT id FROM t_master WHERE name BETWEEN 'ALPHA' AND 'DELTA' ORDER BY id
SELECT t_detail.* FROM t_detail WHERE qty > 3 ORDER BY id
SELECT d.*, m.name FROM t_detail d INNER JOIN t_master m ON d.master_id = m.id ORDER BY d.id
SELECT [t_detail].* FROM [t_detail] WHERE qty > 3 ORDER BY id
