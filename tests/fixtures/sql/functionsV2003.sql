SELECT * FROM t_funcs
SELECT id, num FROM t_funcs WHERE num < 0
SELECT num * 2, num + 0.5 FROM t_funcs
SELECT IIf(num < 0, 'neg', 'pos') FROM t_funcs
SELECT Len(descr) FROM t_funcs
SELECT Format(date0, 'yyyy'), Month(date0), Day(date0) FROM t_funcs
SELECT DateAdd('d', 5, date0) FROM t_funcs
SELECT DateDiff('d', date0, #1/1/2004#) FROM t_funcs
SELECT UCase(descr), Left(descr, 5) FROM t_funcs
SELECT CStr(id) FROM t_funcs
