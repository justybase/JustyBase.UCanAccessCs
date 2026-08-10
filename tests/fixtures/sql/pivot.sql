SELECT * FROM t_pivot
SELECT c_cod, c_val FROM t_pivot WHERE c_val > 100
SELECT c_cod FROM t_pivot ORDER BY c_val DESC
SELECT c_cod FROM t_pivot WHERE c_cod LIKE 'p*'
SELECT c_cod, IIf(c_val > 100, 'big', 'small') FROM t_pivot
SELECT Count(*), Sum(c_val), Max(c_val), Min(c_val) FROM t_pivot
SELECT c_cod FROM t_pivot WHERE c_dt = #5/30/2013 1:18:14 PM#
SELECT Month(c_dt), Year(c_dt), Count(*) FROM t_pivot GROUP BY Month(c_dt), Year(c_dt) ORDER BY Month(c_dt)
SELECT TOP 2 c_cod FROM t_pivot ORDER BY c_cod
