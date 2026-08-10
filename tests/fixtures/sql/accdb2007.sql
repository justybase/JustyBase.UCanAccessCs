SELECT * FROM t_data ORDER BY id
SELECT id, name, val FROM t_data WHERE active = true ORDER BY id
SELECT grp, Count(*) AS n, Sum(val) AS tot FROM t_grp GROUP BY grp ORDER BY grp
SELECT name FROM t_data WHERE name LIKE 'A*' ORDER BY id
SELECT IIf(val > 10, 'big', 'small') AS sz FROM t_grp ORDER BY gid
SELECT Count(DISTINCT grp) AS g FROM t_grp
SELECT id, UCase(name) AS up, Len(name) AS l FROM t_data WHERE id <= 2 ORDER BY id
SELECT Sum(val) AS total FROM t_grp
SELECT id, m FROM t_data WHERE m > 0 ORDER BY id
SELECT id, Format(dt, 'yyyy-mm-dd') AS fmt FROM t_data WHERE id = 1
