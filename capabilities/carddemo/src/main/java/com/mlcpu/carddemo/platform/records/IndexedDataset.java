package com.mlcpu.carddemo.platform.records;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.Optional;
import java.util.TreeMap;

/**
 * A key-sequenced dataset held as fixed-length record images in primary-key order (the KSDS image format the job
 * engine uses). Changes are written back, in key order, on {@link #close()}.
 */
public final class IndexedDataset implements AutoCloseable {
    private final Path path;
    private final int lrecl;
    private final int keyOffset;
    private final int keyLength;
    private final TreeMap<String, byte[]> records = new TreeMap<>();
    private boolean changed;

    private IndexedDataset(Path path, int lrecl, int keyOffset, int keyLength) {
        this.path = path;
        this.lrecl = lrecl;
        this.keyOffset = keyOffset;
        this.keyLength = keyLength;
    }

    /** Opens an existing dataset for reading or update. */
    public static IndexedDataset open(Path path, int lrecl, int keyOffset, int keyLength) throws IOException {
        if (!Files.exists(path)) throw new DatasetException(path + " not found", "35");
        IndexedDataset d = new IndexedDataset(path, lrecl, keyOffset, keyLength);
        byte[] all = Files.readAllBytes(path);
        if (all.length % lrecl != 0) throw new DatasetException(path + " is not a whole number of " + lrecl + "-byte records", "39");
        for (int i = 0; i < all.length; i += lrecl) {
            byte[] r = java.util.Arrays.copyOfRange(all, i, i + lrecl);
            d.records.put(d.keyOf(r), r);
        }
        return d;
    }

    /** Creates an empty dataset (OPEN OUTPUT); it is written on close even when nothing was added. */
    public static IndexedDataset create(Path path, int lrecl, int keyOffset, int keyLength) {
        IndexedDataset d = new IndexedDataset(path, lrecl, keyOffset, keyLength);
        d.changed = true;
        return d;
    }

    public String keyOf(byte[] record) {
        return new String(record, keyOffset, keyLength, StandardCharsets.ISO_8859_1);
    }

    public Optional<byte[]> find(String key) {
        byte[] r = records.get(key);
        return r == null ? Optional.empty() : Optional.of(r.clone());
    }

    /** First record, in primary-key order, whose bytes at {@code offset} equal {@code value} (an alternate-key read). */
    public Optional<byte[]> findBy(int offset, String value) {
        byte[] v = value.getBytes(StandardCharsets.ISO_8859_1);
        outer:
        for (byte[] r : records.values()) {
            for (int i = 0; i < v.length; i++) if (r[offset + i] != v[i]) continue outer;
            return Optional.of(r.clone());
        }
        return Optional.empty();
    }

    /** Adds a record; fails with status 22 when the key exists. */
    public void insert(byte[] record) throws DatasetException {
        String key = keyOf(record);
        if (records.containsKey(key)) throw new DatasetException("duplicate key " + key, "22");
        records.put(key, record.clone());
        changed = true;
    }

    /** Replaces a record; fails with status 23 when the key does not exist. */
    public void replace(byte[] record) throws DatasetException {
        String key = keyOf(record);
        if (!records.containsKey(key)) throw new DatasetException("no record for key " + key, "23");
        records.put(key, record.clone());
        changed = true;
    }

    /** Records in primary-key order. */
    public List<byte[]> inKeyOrder() {
        List<byte[]> out = new ArrayList<>(records.size());
        for (byte[] r : records.values()) out.add(r.clone());
        return out;
    }

    public int lrecl() {
        return lrecl;
    }

    @Override
    public void close() throws IOException {
        if (!changed) return;
        byte[] all = new byte[records.size() * lrecl];
        int i = 0;
        for (byte[] r : records.values()) {
            System.arraycopy(r, 0, all, i, lrecl);
            i += lrecl;
        }
        Files.write(path, all);
        changed = false;
    }
}
