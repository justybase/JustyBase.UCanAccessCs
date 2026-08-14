-- explicit insert while AUTOINCREMENT is enabled
CREATE TABLE t_auto (id AUTOINCREMENT, name TEXT(20));
INSERT INTO t_auto (name) VALUES ('a');
SELECT id, name FROM t_auto;
INSERT INTO t_auto (id, name) VALUES (100, 'b');
SELECT id, name FROM t_auto;
-- disable and insert explicit values
DISABLE AUTOINCREMENT ON t_auto;
INSERT INTO t_auto (id, name) VALUES (200, 'c');
SELECT id, name FROM t_auto;
-- null autonumber while disabled
INSERT INTO t_auto (name) VALUES ('d');
SELECT id, name FROM t_auto;
-- re-enable: counter must resume after max
ENABLE AUTOINCREMENT ON t_auto;
INSERT INTO t_auto (name) VALUES ('e');
SELECT id, name FROM t_auto;
-- create table then disable right after create
CREATE TABLE t_auto2 (id AUTOINCREMENT, name TEXT(20));
DISABLE AUTOINCREMENT ON t_auto2;
INSERT INTO t_auto2 (id, name) VALUES (7, 'x');
SELECT id, name FROM t_auto2;
ENABLE AUTOINCREMENT ON t_auto2;
INSERT INTO t_auto2 (name) VALUES ('y');
SELECT id, name FROM t_auto2;