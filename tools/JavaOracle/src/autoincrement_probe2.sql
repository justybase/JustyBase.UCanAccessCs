CREATE TABLE t_auto (id AUTOINCREMENT, name TEXT(20));
INSERT INTO t_auto (name) VALUES ('a');
DISABLE AUTOINCREMENT ON t_auto;
INSERT INTO t_auto (id, name) VALUES (200, 'c');
SELECT id, name FROM t_auto;
ENABLE AUTOINCREMENT ON t_auto;
INSERT INTO t_auto (name) VALUES ('e');
SELECT id, name FROM t_auto;
-- counter state after enable: insert another
INSERT INTO t_auto (name) VALUES ('f');
SELECT id, name FROM t_auto;
-- disable again, insert below max, then enable and insert: gap must not be reused
DISABLE AUTOINCREMENT ON t_auto;
INSERT INTO t_auto (id, name) VALUES (50, 'low');
SELECT id, name FROM t_auto;
ENABLE AUTOINCREMENT ON t_auto;
INSERT INTO t_auto (name) VALUES ('g');
SELECT id, name FROM t_auto;