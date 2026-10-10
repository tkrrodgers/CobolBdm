package com.mlcpu.platform.records;

import java.io.BufferedOutputStream;
import java.io.IOException;
import java.io.OutputStream;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardOpenOption;

/**
 * A fixed-length sequential print file written with WRITE … ADVANCING, byte for byte as GnuCOBOL writes it:
 * <ul>
 *   <li>{@code WRITE r AFTER n} / {@code AFTER PAGE}: n line feeds (x'0A') or one form feed (x'0C') <b>before</b> the record;</li>
 *   <li>{@code WRITE r BEFORE n} / {@code BEFORE PAGE}: the control <b>after</b> the record;</li>
 *   <li>a plain {@code WRITE r}: the record alone (no separator);</li>
 *   <li>{@code CLOSE}: one line feed when the last ADVANCING write was AFTER (a line is still open).</li>
 * </ul>
 * Records are written at their full length (trailing spaces included).
 */
public final class PrintFile implements AutoCloseable {
    private final OutputStream out;
    private final int lrecl;
    private boolean lineOpen;

    private PrintFile(OutputStream out, int lrecl) {
        this.out = out;
        this.lrecl = lrecl;
    }

    public static PrintFile create(Path path, int lrecl) throws IOException {
        return new PrintFile(new BufferedOutputStream(Files.newOutputStream(path, StandardOpenOption.CREATE, StandardOpenOption.TRUNCATE_EXISTING, StandardOpenOption.WRITE)), lrecl);
    }

    /** WRITE without ADVANCING. */
    public void write(byte[] record) throws IOException {
        out.write(check(record));
    }

    /** WRITE … AFTER ADVANCING {@code lines} LINES. */
    public void writeAfter(byte[] record, int lines) throws IOException {
        for (int i = 0; i < lines; i++) out.write('\n');
        out.write(check(record));
        lineOpen = true;
    }

    /** WRITE … AFTER ADVANCING PAGE. */
    public void writeAfterPage(byte[] record) throws IOException {
        out.write('\f');
        out.write(check(record));
        lineOpen = true;
    }

    /** WRITE … BEFORE ADVANCING {@code lines} LINES. */
    public void writeBefore(byte[] record, int lines) throws IOException {
        out.write(check(record));
        for (int i = 0; i < lines; i++) out.write('\n');
        lineOpen = false;
    }

    /** WRITE … BEFORE ADVANCING PAGE. */
    public void writeBeforePage(byte[] record) throws IOException {
        out.write(check(record));
        out.write('\f');
        lineOpen = false;
    }

    private byte[] check(byte[] record) throws DatasetException {
        if (record.length != lrecl) throw new DatasetException("record of " + record.length + " bytes for LRECL " + lrecl, "44");
        return record;
    }

    @Override
    public void close() throws IOException {
        if (lineOpen) out.write('\n');
        lineOpen = false;
        out.close();
    }
}
