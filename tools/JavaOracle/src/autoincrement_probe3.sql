CREATE TABLE t_plain (id LONG, name TEXT(20));
INSERT INTO t_plain (id, name) VALUES (1, 'x');
DISABLE AUTOINCREMENT ON t_plain;
INSERT INTO t_plain (id, name) VALUES (2, 'y');
DISABLE AUTOINCREMENT ON t_nonexistent;
ENABLE AUTOINCREMENT ON t_nonexistent;