package com.mlcpu.platform.records;

import com.mlcpu.rt.OpenDatasets;
import java.io.BufferedInputStream;
import java.io.BufferedOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardOpenOption;
import java.util.Optional;

/**
 * Variable-length sequential datasets (RECORDING MODE V / RECORD VARYING): each record is a 4-byte record descriptor word
 * (2-byte big-endian length, then two zero bytes) followed by that many bytes — GnuCOBOL's default variable format and
 * the job engine's. A record's length is the length of the record description it was written FROM.
 */
public final class VariableDataset {
    private VariableDataset() {}

    public static Reader reader(Path path) throws IOException {
        if (!Files.exists(path)) throw new DatasetException(path + " not found", "35");
        return new Reader(new BufferedInputStream(Files.newInputStream(path)));
    }

    public static Writer writer(Path path) throws IOException {
        return new Writer(new BufferedOutputStream(Files.newOutputStream(path, StandardOpenOption.CREATE, StandardOpenOption.TRUNCATE_EXISTING, StandardOpenOption.WRITE)));
    }

    public static final class Reader implements AutoCloseable {
        private final InputStream in;

        Reader(InputStream in) {

            OpenDatasets.opened(this);
            this.in = in;
        }

        /** The next record (exactly its own length), or empty at end of file. */
        public Optional<byte[]> next() throws IOException {
            byte[] rdw = in.readNBytes(4);
            if (rdw.length == 0) return Optional.empty();
            if (rdw.length < 4) throw new DatasetException("truncated record descriptor", "04");
            int len = ((rdw[0] & 0xFF) << 8) | (rdw[1] & 0xFF);
            byte[] r = in.readNBytes(len);
            if (r.length < len) throw new DatasetException("short record of " + r.length + " bytes (descriptor says " + len + ")", "04");
            return Optional.of(r);
        }

        @Override
        public void close() throws IOException {
            OpenDatasets.closed(this);
            in.close();
        }
    }

    public static final class Writer implements AutoCloseable {
        private final OutputStream out;

        Writer(OutputStream out) {

            OpenDatasets.opened(this);
            this.out = out;
        }

        /** Writes one record; its length is the array's length. */
        public void write(byte[] record) throws IOException {
            if (record.length > 0xFFFF) throw new DatasetException("record of " + record.length + " bytes is too long", "44");
            out.write((record.length >> 8) & 0xFF);
            out.write(record.length & 0xFF);
            out.write(0);
            out.write(0);
            out.write(record);
        }

        @Override
        public void close() throws IOException {
            OpenDatasets.closed(this);
            out.close();
        }
    }
}
