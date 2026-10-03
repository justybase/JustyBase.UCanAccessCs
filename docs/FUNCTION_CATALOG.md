# Function parity catalog (Java UCanAccess 5.1.7 vs .NET provider)

Source for the Java column: `20-getting-started.html` (spannm/ucanaccess 5.1.7).
Source for the .NET column: `AccessFunctions.Register` in `src/UCanAccess`.

Legend: ✅ supported, ❌ unsupported/missing. SQLite built-ins (`iif`) count as
supported where noted. Domain/aggregate/financial names are case-insensitive in
both stacks.

## Scalar / conversion / string / date

| Java | .NET | Notes |
|---|---|---|
| ASC | ✅ `asc` | |
| ATN | ✅ `atn` | `Math.Atan` |
| CBOOL | ✅ `cbool` | |
| CCUR | ✅ `ccur` | exact-decimal |
| CDATE | ✅ `cdate` | |
| CDBL | ✅ `cdbl` | |
| CDEC | ✅ `cdec` | exact-decimal |
| CINT | ✅ `cint` | banker's? `Math.Round` |
| CLONG | ✅ `clong`/`clng` | alias |
| CSIGN | ✅ `csign` | Java-compatible |
| CSTR | ✅ `cstr` | |
| CVAR | ✅ `cvar` | string |
| DATEADD | ✅ `dateadd` | |
| DATEDIFF | ✅ `datediff` | |
| DATEPART | ✅ `datepart` | |
| DATE | ✅ `date` | |
| DATESERIAL | ✅ `dateserial` | |
| DATEVALUE | ✅ `datevalue` | |
| FIX | ✅ `fix` | truncate |
| FORMAT | ✅ `format` | subset |
| IIF | ✅ `iif` | SQLite built-in |
| INSTR | ✅ `instr` | |
| INSTRREV | ✅ `instrrev` | |
| ISDATE | ✅ `isdate` | |
| ISNUMERIC | ✅ `isnumeric` | |
| INT | ✅ `int` | floor |
| IsNull | ✅ `access_isnull` | rewritten (SQLite keyword) |
| LEN | ✅ `len` | |
| MID | ✅ `mid` | |
| MONTHNAME | ✅ `monthname` | |
| NOW | ✅ `now` | freezable clock in tests |
| NZ | ✅ `nz` | |
| PARTITION | ✅ `partition` | |
| SIGN | ✅ `sign`/`sgn` | alias |
| SPACE | ✅ `space` | |
| SQR | ✅ `sqr` | `Math.Sqrt` |
| STR | ✅ `str` | leading space for positives |
| STRING | ✅ `string` | `String(char, count)` |
| STRCOMP | ✅ `strcomp` | binary/text modes |
| STRCONV | ✅ `strconv` | |
| STRREVERSE | ✅ `strreverse` | |
| SWITCH | ✅ `switch` | |
| RND | ✅ `rnd` | |
| TIME | ✅ `time` | |
| TIMESERIAL | ✅ `timeserial` | |
| VAL | ✅ `val` | |
| WEEKDAY | ✅ `weekday` | |
| WEEKDAYNAME | ✅ `weekdayname` | |
| COS/SIN | ✅ `cos`/`sin` (+`acos/asin/atan/tan/exp/log`) | HSQLDB subset extended |
| LTRIM/RTRIM | ✅ `ltrim`/`rtrim`/`trim` | |
| UCASE/LCASE | ✅ `ucase`/`lcase` | |
| Complex `Equals/EqualsIgnoreOrder/Contains` | ✅ | JSON mirror + array params (`ComplexTypeProviderTests`) |

## Aggregates and domain aggregates

| Java | .NET | Notes |
|---|---|---|
| COUNT/AVG/SUM/MAX/MIN | ✅ SQLite + exact-decimal | |
| STDEV/STDEVP/VAR/VARP | ✅ `stdev/stdevp/var/varp` | |
| FIRST/LAST | ✅ `first/last` | |
| DCOUNT/DAVG/DSUM/DMAX/DMIN/DFIRST/DLAST/DLOOKUP | ✅ `d*` | same-connection subqueries |
| DSTDEV/DSTDEVP/DVAR/DVARP | ✅ `dstdev/dstdevp/dvar/dvarp` | extension beyond Java list |

## Financial

| Java | .NET | Notes |
|---|---|---|
| MT | ❌ | Decided: no such Access/VBA function, and no `@FunctionType("MT")` exists in Java `Functions.java` (checked against spannm/ucanaccess master). Almost certainly a typo in the UCanAccess docs page (intended `MIRR`, which is supported below). Stays unimplemented by design; `IRR` (real Access function, absent from Java too) is a future extension candidate, not parity. |
| NPER/IPMT/PPMT/RATE/PV/FV/DDB/SYD/SLN/PMT | ✅ | periodic rate as fraction (0.10 = 10%) |
| MIRR/NPV | ✅ | .NET extension (Access-compatible) |

## Evidence rule

New `✅` rows require a `FunctionsTests` case. New `❌` rows require a reason
here, not a silent omission.
