package com.mlcpu.carddemo.platform.batch;

import com.mlcpu.carddemo.platform.records.DatasetException;
import com.mlcpu.rt.ModernStep;
import java.io.IOException;
import java.util.function.Consumer;

/**
 * The batch failure convention of the CardDemo programs: an unexpected dataset status displays what failed and the
 * status as {@code FILE STATUS IS: NNNN} plus four digits, then the step abends with user code 999.
 */
public final class BatchFailure {
    private BatchFailure() {}

    public static final int ABEND_CODE = 999;

    public static ModernStep.Abend abend(Consumer<String> console, String what, String status) {
        console.accept(what);
        console.accept("FILE STATUS IS: NNNN" + statusDigits(status));
        console.accept("ABENDING PROGRAM");
        return new ModernStep.Abend(ABEND_CODE, what + " (status " + status + ")");
    }

    public static ModernStep.Abend abend(Consumer<String> console, String what, IOException e) {
        return abend(console, what, e instanceof DatasetException d ? d.status() : "30");
    }

    public interface Opener<T> {
        T open() throws IOException;
    }

    /** Opens a dataset; a failure abends with the program's message for that dataset. */
    public static <T> T open(Consumer<String> console, String what, Opener<T> opener) {
        try {
            return opener.open();
        } catch (IOException e) {
            throw abend(console, what, e);
        }
    }

    public static void close(Consumer<String> console, String what, AutoCloseable c) {
        try {
            c.close();
        } catch (IOException e) {
            throw abend(console, what, e);
        } catch (Exception e) {
            throw abend(console, what, new IOException(e));
        }
    }

    /** Numeric statuses print as 00 + status; a '9x' or non-numeric status prints as its first byte and a binary value. */
    static String statusDigits(String status) {
        String s = status.length() >= 2 ? status.substring(0, 2) : (status + "0").substring(0, 2);
        if (Character.isDigit(s.charAt(0)) && Character.isDigit(s.charAt(1)) && s.charAt(0) != '9') return "00" + s;
        return s.charAt(0) + String.format("%03d", (int) s.charAt(1));
    }
}
