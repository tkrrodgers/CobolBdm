# Distance to original: joda-money

Tool: jobol-audit compare 1.0.0. Original: `C:/Users/Tomrodgers/JavaToCOBOLTest/claude-in-app/workspace/packages/jodaorg-joda-money-39d5bbf/source/src/main/java`. Modern: `C:/Users/Tomrodgers/Source/ModernizedJava/joda-money-loop4-iter6/src/main/java`. Scope: reached (J3S COBOL `C:/Users/Tomrodgers/Source/CobolBdm/projects/joda-money/source`).

## Summary

| Measure | Value |
|---|---|
| API preservation | 33.8% (70/207) |
| API preservation, reached by J3S | 93.3% (70/75) |
| Name fidelity | 84.2% (187/222) |
| Signature fidelity | 34.4% (76/221) |
| Signature fidelity, reached by J3S | 97.4% (76/78) |
| Original types in scope | 9 (14 top-level out of scope) |
| Matched | 9 (9 by name, 0 moved) |
| Original only | 0 |
| Modern only | 36 pipeline infrastructure, 0 Lombok expansion, 0 other |

API preservation counts the public and protected fields, methods, constructors and enum constants of the original's public types that are present in modern with the same name, types, static flag and visibility. Name fidelity is the share of aligned identifier pairs (fields, methods, parameters, locals) whose modern spelling is exact. Signature fidelity is the share of original methods and constructors present with the same name, parameter types, return type and static flag. "Reached" leaves out methods and types no J3S frame was generated for.

## Names

| Element | exact | case | truncated | abbreviated | accessor_style | truncated_numbered | numbered | renamed |
|---|---|---|---|---|---|---|---|---|
| field | 19 | 2 | 0 | 1 | 0 | 0 | 0 | 0 |
| local | 34 | 0 | 5 | 0 | 0 | 0 | 0 | 0 |
| method | 77 | 0 | 1 | 0 | 0 | 0 | 0 | 0 |
| param | 57 | 0 | 25 | 0 | 0 | 0 | 1 | 0 |

Length of the modern spelling of truncated names: 10:11, 11:20

Completion of truncated names from identifiers the modern estate spells out elsewhere: ambiguous-correct 2, none 28, unique-correct 1

## Structure (matched and original-only types against their modern counterparts)

| | Types | LOC | Methods | Avg method lines | Max method lines | Branches | Constructors | Factories | Builders | equals+hashCode | toString |
|---|---|---|---|---|---|---|---|---|---|---|---|
| original | 9 | 1238 | 221 | 5.3 | 48 | 130 | 8 | 38 | 0 | 3 | 3 |
| modern | 9 | 556 | 78 | 6.3 | 41 | 69 | 7 | 9 | 0 | 3 | 2 |

## Idioms

"Original" counts the original members that have a modern counterpart (the comparable part); "original, all" also counts the members and types J3S never compiled.

| Idiom | Original, all | Original | Modern | Delta |
|---|---|---|---|---|
| bigdecimal | 125 | 49 | 59 | +10 |
| biginteger | 2 | 2 | 0 | -2 |
| collection | 10 | 1 | 2 | +1 |
| exceptionBare | 0 | 0 | 9 | +9 |
| exceptionWithArgs | 29 | 16 | 4 | -12 |
| interface | 1 | 1 | 1 | 0 |
| list | 15 | 14 | 14 | 0 |
| loop | 16 | 9 | 9 | 0 |
| map | 4 | 4 | 5 | +1 |
| nullCheck | 36 | 14 | 14 | 0 |
| patternMatch | 2 | 2 | 0 | -2 |
| record | 0 | 0 | 2 | +2 |
| set | 3 | 3 | 3 | 0 |
| switchExpr | 1 | 0 | 0 | 0 |
| var | 70 | 33 | 0 | -33 |

Type uses (declarations, generics, `new`, static calls):

| Type | Original, all | Original | Modern |
|---|---|---|---|
| ArrayList | 4 | 4 | 4 |
| BigDecimal | 75 | 33 | 41 |
| BigInteger | 2 | 2 | 0 |
| Collections | 1 | 1 | 2 |
| ConcurrentHashMap | 1 | 1 | 1 |
| ConcurrentMap | 3 | 3 | 0 |
| HashSet | 1 | 1 | 1 |
| Iterable | 8 | 0 | 0 |
| Iterator | 1 | 0 | 0 |
| List | 11 | 10 | 10 |
| Map | 0 | 0 | 4 |
| RoundingMode | 50 | 16 | 18 |
| Set | 2 | 2 | 2 |

## Top 9 types by distance

Distance is a sum of penalties (weights in COMPARE-GAPS.md: kind 5, lost supertype 3, missing API method 3, other reached missing method 2, changed signature 2, missing field 2, extra method 1, member name 1, parameter/local name 0.25, ...). Methods J3S never compiled are not penalised.

| Type | Distance | Per member | Largest contributions |
|---|---|---|---|
| `org.joda.money.CurrencyUnit` | 35.5 | 0.83 | supertypes 6.0, field types 5.0, kind 5.0 |
| `org.joda.money.Money` | 20.3 | 0.26 | supertypes 9.0, kind 5.0, missing fields 2.0 |
| `org.joda.money.BigMoney` | 18.0 | 0.2 | supertypes 6.0, names 3.5, idioms 2.0 |
| `org.joda.money.CurrencyUnitDataProvider` | 9.0 | 3.0 | kind 5.0, member flags 2.0, extra methods 1.0 |
| `org.joda.money.CurrencyMismatchException` | 6.0 | 1.0 | supertypes 4.0, missing fields 2.0 |
| `org.joda.money.DefaultCurrencyUnitDataProvider` | 6.0 | 0.86 | visibility 2.0, extra methods 1.0, field flags 1.0 |
| `org.joda.money.IllegalCurrencyException` | 6.0 | 3.0 | supertypes 4.0, missing fields 2.0 |
| `org.joda.money.MoneyUtils` | 2.0 | 0.13 | member flags 1.0, modifiers 1.0 |
| `org.joda.money.BigMoneyProvider` | 0.0 | 0.0 |  |

## Gaps by pattern

| Pattern | Count | Examples |
|---|---|---|
| FIELD.MISSING_SERIAL_VERSION | 5 | `BigMoney` serialVersionUID: static final long serialVersionUID → - (source)<br>`CurrencyMismatchException` serialVersionUID: static final long serialVersionUID → - (source)<br>`CurrencyUnit` serialVersionUID: static final long serialVersionUID → - (source) |
| IDIOM.EXCEPTION_MESSAGE_LOST | 3 | `BigMoney`: with message 4 → with message 1, bare 2<br>`CurrencyUnit`: with message 10 → with message 3, bare 6<br>`DefaultCurrencyUnitDataProvider`: with message 1 → with message 0, bare 1 |
| IDIOM.PATTERNMATCH_LOST | 2 | `BigMoney`: 1 → 0 (-1)<br>`Money`: 1 → 0 (-1) |
| MEMBER.VISIBILITY_NARROWED_METHOD | 2 | `BigMoney` getScale(): public → private (method)<br>`CurrencyUnit` registerCurrency(String,int,int,List,boolean): public → private (method) |
| MEMBER.VISIBILITY_WIDENED_CTOR | 3 | `BigMoney` &lt;init&gt;(CurrencyUnit,BigDecimal): package → public (constructor)<br>`CurrencyUnit` &lt;init&gt;(String,short,short): package → public (constructor)<br>`Money` &lt;init&gt;(BigMoney): package → public (constructor) |
| MEMBER.VISIBILITY_WIDENED_METHOD | 4 | `CurrencyUnitDataProvider` registerCountry(String,String): protected → public (method)<br>`CurrencyUnitDataProvider` registerCurrency(String,int,int): protected → public (method)<br>`DefaultCurrencyUnitDataProvider` registerCurrencies(): protected → public (method) |
| MEMBER.VISIBILITY_WIDENED_STATIC_FIELD | 7 | `BigMoney` PARSE_REGEX: private → public (field)<br>`CurrencyUnit` CODE: private → public (field)<br>`CurrencyUnit` currenciesByCode: private → public (field) |
| METHOD.EXTRA_CTOR | 2 | `CurrencyUnitDataProvider` &lt;init&gt;(): - → &lt;init&gt;() (source public)<br>`DefaultCurrencyUnitDataProvider` &lt;init&gt;(): - → &lt;init&gt;() (source public) |
| METHOD.EXTRA_RECORD | 4 | `CurrencyUnit` code(): - → String code() (record public)<br>`CurrencyUnit` decimalPlaces(): - → int decimalPlaces() (record public)<br>`CurrencyUnit` numericCode(): - → int numericCode() (record public) |
| METHOD.UNREACHED_ACCESSOR | 24 | `BigMoney` getAmountMajor(): BigDecimal getAmountMajor() → - (source public)<br>`BigMoney` getAmountMajorInt(): int getAmountMajorInt() → - (source public)<br>`BigMoney` getAmountMajorLong(): long getAmountMajorLong() → - (source public) |
| METHOD.UNREACHED_API | 107 | `BigMoney` abs(): BigMoney abs() → - (source public)<br>`BigMoney` convertRetainScale(CurrencyUnit,BigDecimal,RoundingMode): BigMoney convertRetainScale(CurrencyUnit,BigDecimal,RoundingMode) → - (source public)<br>`BigMoney` dividedBy(double,RoundingMode): BigMoney dividedBy(double,RoundingMode) → - (source public) |
| METHOD.UNREACHED_CTOR | 3 | `BigMoney` &lt;init&gt;(): &lt;init&gt;() → - (source private)<br>`Money` &lt;init&gt;(): &lt;init&gt;() → - (source private)<br>`MoneyUtils` &lt;init&gt;(): &lt;init&gt;() → - (source private) |
| METHOD.UNREACHED_OBJECT | 1 | `BigMoney` toString(): String toString() → - (source public) |
| METHOD.UNREACHED_PRIVATE | 8 | `BigMoney` checkCurrencyEqual(BigMoneyProvider): BigMoney checkCurrencyEqual(BigMoneyProvider) → - (source private)<br>`BigMoney` readObject(ObjectInputStream): void readObject(ObjectInputStream) → - (source private)<br>`BigMoney` with(BigDecimal): BigMoney with(BigDecimal) → - (source private) |
| NAME.ABBREVIATED | 1 | `CurrencyUnit` currenciesByNumericCode: currenciesByNumericCode → CURRENCIES_BY_NUMERIC_C (field) |
| NAME.CASE | 2 | `CurrencyUnit` currenciesByCode: currenciesByCode → CURRENCIES_BY_CODE (field)<br>`CurrencyUnit` currenciesByCountry: currenciesByCountry → CURRENCIES_BY_COUNTRY (field) |
| NAME.NUMBERED | 1 | `CurrencyUnitDataProvider` registerCountry(String,String): countryCode → countryCode3 (param) |
| NAME.TRUNCATED.AMBIGUOUS_CORRECT | 2 | `CurrencyUnit` registerCurrency(String,int,int,List,boolean): countryCodes → countryCode (param, completion ambiguous-correct)<br>`CurrencyUnit` registerCurrency(String,int,int,boolean): countryCodes → countryCode (local, completion ambiguous-correct) |
| NAME.TRUNCATED.NONE | 28 | `BigMoney` convertedTo(CurrencyUnit,BigDecimal): conversionMultipler → conversionM (param, completion none)<br>`BigMoney` dividedBy(BigDecimal,RoundingMode): valueToDivideBy → valueToDiv (param, completion none)<br>`BigMoney` minus(BigDecimal): amountToSubtract → amountToSu (param, completion none) |
| NAME.TRUNCATED.UNIQUE_CORRECT | 1 | `BigMoney` getCurrencyUnit(): getCurrencyUnit → getCurrency (method, completion unique-correct) |
| SUPERTYPE.ADDED | 2 | `CurrencyMismatchException`: - → RuntimeException<br>`IllegalCurrencyException`: - → RuntimeException |
| SUPERTYPE.EXTENDS_LOST | 2 | `CurrencyMismatchException`: IllegalArgumentException → -<br>`IllegalCurrencyException`: IllegalArgumentException → - |
| SUPERTYPE.INTERFACE_LOST | 1 | `Money`: BigMoneyProvider → - |
| SUPERTYPE.JDK_INTERFACE_LOST | 6 | `BigMoney`: Comparable&lt;BigMoneyProvider&gt; → -<br>`BigMoney`: Serializable → -<br>`CurrencyUnit`: Comparable&lt;CurrencyUnit&gt; → - |
| TYPE.FINAL_LOST | 2 | `BigMoney`: final → -<br>`MoneyUtils`: final → - |
| TYPE.KIND_ABSTRACT_TO_CLASS | 1 | `CurrencyUnitDataProvider`: abstract → class |
| TYPE.KIND_CLASS_TO_RECORD | 2 | `CurrencyUnit`: class → record<br>`Money`: class → record |
| TYPE.VISIBILITY_WIDENED | 1 | `DefaultCurrencyUnitDataProvider`: package → public |
| TYPEREF.COLLECTION_CHANGED | 3 | `CurrencyUnit` currenciesByCode: ConcurrentMap&lt;String,CurrencyUnit&gt; → Map&lt;String,CurrencyUnit&gt; (field)<br>`CurrencyUnit` currenciesByCountry: ConcurrentMap&lt;String,CurrencyUnit&gt; → Map&lt;String,CurrencyUnit&gt; (field)<br>`CurrencyUnit` currenciesByNumericCode: ConcurrentMap&lt;Integer,CurrencyUnit&gt; → Map&lt;Integer,CurrencyUnit&gt; (field) |
| TYPEREF.NUMERIC_CHANGED | 4 | `CurrencyUnit` &lt;init&gt;(String,short,short): short → int (param 2)<br>`CurrencyUnit` &lt;init&gt;(String,short,short): short → int (param 3)<br>`CurrencyUnit` decimalPlaces: short → int (field) |
| TYPEREF.TO_OBJECT_KNOWN | 1 | `BigMoney` PARSE_REGEX: Pattern → Object (field) |

## Matched types

| Original | Modern | Kind | API | Signatures | Fields m/x | Methods m/x | Distance |
|---|---|---|---|---|---|---|---|
| `BigMoney` | same | class | 32.9% | 33.0% | 1/0 | 58/0 | 18.0 |
| `BigMoneyProvider` | same | interface | 100.0% | 100.0% | 0/0 | 0/0 | 0.0 |
| `CurrencyMismatchException` | same | class | 33.3% | 33.3% | 1/0 | 2/0 | 6.0 |
| `CurrencyUnit` | same | class → record | 68.8% | 57.1% | 1/0 | 11/3 | 35.5 |
| `CurrencyUnitDataProvider` | same | abstract → class | 0.0% | 66.7% | 0/0 | 1/1 | 9.0 |
| `DefaultCurrencyUnitDataProvider` | same | class | - | 100.0% | 0/0 | 0/1 | 6.0 |
| `IllegalCurrencyException` | same | class | 100.0% | 100.0% | 1/0 | 0/0 | 6.0 |
| `Money` | same | class → record | 25.0% | 26.0% | 1/0 | 57/1 | 20.3 |
| `MoneyUtils` | same | class | 0.0% | 6.7% | 0/0 | 14/0 | 2.0 |

## Modern-only types

36 pipeline infrastructure types (the generated job, platform, record and strangler layers; not gaps). By rule: glob:**/batch/** 6, lane:compat 30.

## Out of scope (not compiled by J3S, no modern counterpart)

- `org.joda.money.Ser`
- `org.joda.money.format.AmountPrinterParser`
- `org.joda.money.format.GroupingStyle`
- `org.joda.money.format.LiteralPrinterParser`
- `org.joda.money.format.MoneyAmountStyle`
- `org.joda.money.format.MoneyFormatException`
- `org.joda.money.format.MoneyFormatter`
- `org.joda.money.format.MoneyFormatterBuilder`
- `org.joda.money.format.MoneyParseContext`
- `org.joda.money.format.MoneyParser`
- `org.joda.money.format.MoneyPrintContext`
- `org.joda.money.format.MoneyPrinter`
- `org.joda.money.format.MultiPrinterParser`
- `org.joda.money.format.SignedPrinterParser`

