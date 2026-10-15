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

/** Fixed-length sequential datasets: one record image per {@code lrecl} bytes. */
public final class SequentialDataset {
    private SequentialDataset() {}

    public static Reader reader(Path path, int lrecl) throws IOException {
        if (!Files.exists(path)) throw new DatasetException(path + " not found", "35");
        return new Reader(new BufferedInputStream(Files.newInputStream(path)), lrecl);
    }

    public static Writer writer(Path path, int lrecl) throws IOException {
        return new Writer(new BufferedOutputStream(Files.newOutputStream(path, StandardOpenOption.CREATE, StandardOpenOption.TRUNCATE_EXISTING, StandardOpenOption.WRITE)), lrecl);
    }

    public static final class Reader implements AutoCloseable {
        private final InputStream in;
        private final int lrecl;

        Reader(InputStream in, int lrecl) {

            OpenDatasets.opened(this);
            this.in = in;
            this.lrecl = lrecl;
        }

        /** The next record image, or empty at end of file. */
        public Optional<byte[]> next() throws IOException {
            byte[] r = in.readNBytes(lrecl);
            if (r.length == 0) return Optional.empty();
            if (r.length < lrecl) throw new DatasetException("short record of " + r.length + " bytes", "04");
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
        private final int lrecl;

        Writer(OutputStream out, int lrecl) {

            OpenDatasets.opened(this);
            this.out = out;
            this.lrecl = lrecl;
        }

        public void write(byte[] record) throws IOException {
            if (record.length != lrecl) throw new DatasetException("record of " + record.length + " bytes for LRECL " + lrecl, "44");
            out.write(record);
        }

        @Override
        public void close() throws IOException {
            OpenDatasets.closed(this);
            out.close();
        }
    }
}
