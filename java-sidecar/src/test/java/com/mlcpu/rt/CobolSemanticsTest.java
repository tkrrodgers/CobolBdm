package com.mlcpu.rt;

import static org.junit.jupiter.api.Assertions.assertEquals;

import java.math.BigDecimal;
import org.junit.jupiter.api.Test;

/** Golden vectors observed from the GnuCOBOL oracle (-std=ibm -fsign=EBCDIC); see tools/carddemo. */
class CobolSemanticsTest {

    private static CobField num(int digits, int scale, boolean signed, CobField.Use use) {
        int len = switch (use) {
            case PACKED -> digits / 2 + 1;
            case BINARY -> digits <= 4 ? 2 : digits <= 9 ? 4 : 8;
            default -> digits;
        };
        return CobField.num("T", use, new Area(len), 0, len, digits, scale, signed, false, false, null, null);
    }

    private static CobField edited(String pic) {
        String p = Edit.expand(pic);
        CobField probe = CobField.edited("E", CobField.Cat.NUM_EDITED, new Area(p.length()), 0, p.length(), 11, 2, false, p, null, null);
        return probe;
    }

    @Test
    void editedPicturesMatchOracle() {
        assertEquals("               ", Edit.format(BigDecimal.ZERO, Edit.expand("-ZZZ,ZZZ,ZZZ.ZZ"), 2, false));
        assertEquals("          12.50", Edit.format(new BigDecimal("12.5"), Edit.expand("-ZZZ,ZZZ,ZZZ.ZZ"), 2, false));
        assertEquals("-      1,234.56", Edit.format(new BigDecimal("-1234.56"), Edit.expand("-ZZZ,ZZZ,ZZZ.ZZ"), 2, false));
        assertEquals("+         12.50", Edit.format(new BigDecimal("12.5"), Edit.expand("+ZZZ,ZZZ,ZZZ.ZZ"), 2, false));
        assertEquals("    0.00 ", Edit.format(BigDecimal.ZERO, Edit.expand("Z,ZZ9.99-"), 2, false));
        assertEquals("   12.50 ", Edit.format(new BigDecimal("12.5"), Edit.expand("Z,ZZ9.99-"), 2, false));
        assertEquals("1,234.56-", Edit.format(new BigDecimal("-1234.56"), Edit.expand("Z,ZZ9.99-"), 2, false));
        assertEquals("     $0.00", Edit.format(BigDecimal.ZERO, Edit.expand("$$$,$$9.99"), 2, false));
        assertEquals("    $12.50", Edit.format(new BigDecimal("12.5"), Edit.expand("$$$,$$9.99"), 2, false));
        assertEquals(" $1,234.56", Edit.format(new BigDecimal("-1234.56"), Edit.expand("$$$,$$9.99"), 2, false));
    }

    @Test
    void signedZonedUsesEbcdicOverpunch() {
        CobField f = num(11, 2, true, CobField.Use.DISPLAY);
        f.setDec(new BigDecimal("19.40"), false);
        assertEquals("0000000194{", f.str());
        f.setDec(new BigDecimal("-1234.56"), false);
        assertEquals("0000012345O", f.str());
        assertEquals(new BigDecimal("-1234.56"), f.dec());
    }

    @Test
    void displayFollowsDialect() {
        CobConfig.DISPLAY_NUMERIC = "gnucobol";
        try {
            CobField z = num(11, 2, true, CobField.Use.DISPLAY);
            z.setDec(new BigDecimal("12.5"), false);
            assertEquals("00000001250+", CobRt.displayText(z));
            CobField p = num(7, 2, true, CobField.Use.PACKED);
            p.setDec(new BigDecimal("-3.25"), false);
            assertEquals("-0000325", CobRt.displayText(p));
            CobField b = num(9, 0, true, CobField.Use.BINARY);
            b.setDec(new BigDecimal("16"), false);
            assertEquals("+0000000016", CobRt.displayText(b));
            assertEquals("-45", CobRt.displayText(new NumLit("-4.5")));
        } finally {
            CobConfig.DISPLAY_NUMERIC = "zos";
        }
    }

    @Test
    void movesFollowCobolRules() {
        CobField n2 = num(2, 0, false, CobField.Use.DISPLAY);
        CobRt.move(" 7", n2);
        assertEquals("07", n2.str());
        CobField x5 = CobField.alnum("X", new Area(5), 0, 5, false, null, null);
        CobField z3 = num(9, 0, false, CobField.Use.DISPLAY);
        z3.setDec(new BigDecimal("42"), false);
        CobRt.move(z3, x5);
        assertEquals("00000", x5.str());
        CobField nov = num(3, 0, false, CobField.Use.DISPLAY);
        nov.setDec(new BigDecimal("999"), false);
        CobRt.store(nov, nov.dec().add(BigDecimal.ONE), false, false);
        assertEquals("000", nov.str());
        CobField e = edited("-ZZZ,ZZZ,ZZZ.ZZ");
        CobRt.move(new NumLit("-1234.56"), e);
        assertEquals("-      1,234.56", e.str());
    }

    @Test
    void deEditingKeepsTrailingSign() {
        assertEquals(new BigDecimal("-12"), Edit.deEdit("  12-".getBytes(), 0));
        assertEquals(new BigDecimal("-1234.50"), Edit.deEdit("1,234.50CR".getBytes(), 2));
    }

    @Test
    void comparisonPadsAlphanumericWithSpaces() {
        assertEquals(0, CobRt.compare("AB", "AB   "));
        CobField n = num(11, 0, false, CobField.Use.DISPLAY);
        n.setDec(BigDecimal.ONE, false);
        CobField x = CobField.alnum("X", new Area(11), 0, 11, false, null, null);
        x.fill((byte) ' ');
        assertEquals(1, Integer.signum(CobRt.compare(n, x)));
    }
}
