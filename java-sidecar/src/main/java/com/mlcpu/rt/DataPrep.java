package com.mlcpu.rt;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Comparator;
import java.util.List;

/**
 * Dataset preparation (IDCAMS REPRO equivalents) for line-oriented sample data:
 * <pre>
 *   DataPrep fixed --in a.txt --out a.dat --lrecl 350              text lines → fixed records (space padded)
 *   DataPrep ksds  --in a.txt --out a.ksds --lrecl 300 --key 1,11  text lines → indexed image (sorted, unique key)
 *   DataPrep lines --in a.dat --out a.txt --lrecl 133              fixed records → text lines (for review)
 * </pre>
 * Key positions are 1-based (offset,length) as in IDCAMS KEYS(length offset+1).
 */
public final class DataPrep {
    private DataPrep() {}

    public static void main(String[] args) throws IOException {
        String cmd = args[0];
        String in = null, out = null;
        int lrecl = 0, keyPos = 1, keyLen = 0;
        boolean fixedIn = false;
        for (int i = 1; i < args.length; i++) {
            switch (args[i]) {
                case "--in" -> in = args[++i];
                case "--out" -> out = args[++i];
                case "--lrecl" -> lrecl = Integer.parseInt(args[++i]);
                case "--fixed-in" -> fixedIn = true;
                case "--key" -> {
                    String[] p = args[++i].split(",");
                    keyPos = Integer.parseInt(p[0]);
                    keyLen = Integer.parseInt(p[1]);
                }
                default -> throw new IllegalArgumentException(args[i]);
            }
        }
        Path outPath = Path.of(out);
        if (outPath.getParent() != null) Files.createDirectories(outPath.getParent());
        switch (cmd) {
            case "fixed" -> write(outPath, readLines(Path.of(in), lrecl));
            case "ksds" -> {
                List<byte[]> recs = fixedIn ? readFixed(Path.of(in), lrecl) : readLines(Path.of(in), lrecl);
                int ko = keyPos - 1, kl = keyLen;
                Comparator<byte[]> byKey = (a, b) -> Arrays.compareUnsigned(a, ko, ko + kl, b, ko, ko + kl);
                recs.sort(byKey);
                List<byte[]> unique = new ArrayList<>();
                for (byte[] r : recs) {
                    if (!unique.isEmpty() && byKey.compare(unique.get(unique.size() - 1), r) == 0) {
                        System.err.println("duplicate key rejected: " + new String(r, ko, kl, StandardCharsets.ISO_8859_1));
                        continue;
                    }
                    unique.add(r);
                }
                write(outPath, unique);
                System.out.println(outPath + ": " + unique.size() + " records");
            }
            case "lines" -> {
                byte[] all = Files.readAllBytes(Path.of(in));
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i + lrecl <= all.length; i += lrecl) sb.append(new String(all, i, lrecl, StandardCharsets.ISO_8859_1)).append('\n');
                Files.writeString(outPath, sb.toString(), StandardCharsets.ISO_8859_1);
            }
            default -> throw new IllegalArgumentException("unknown command " + cmd);
        }
    }

    static List<byte[]> readFixed(Path p, int lrecl) throws IOException {
        byte[] all = Files.readAllBytes(p);
        List<byte[]> recs = new ArrayList<>();
        for (int i = 0; i + lrecl <= all.length; i += lrecl) recs.add(Arrays.copyOfRange(all, i, i + lrecl));
        return recs;
    }

    static List<byte[]> readLines(Path p, int lrecl) throws IOException {
        byte[] all = Files.readAllBytes(p);
        List<byte[]> recs = new ArrayList<>();
        int start = 0;
        for (int i = 0; i <= all.length; i++) {
            if (i == all.length || all[i] == '\n') {
                int end = i;
                if (end > start && all[end - 1] == '\r') end--;
                if (i == all.length && end == start) break;
                byte[] r = new byte[lrecl];
                Arrays.fill(r, (byte) ' ');
                System.arraycopy(all, start, r, 0, Math.min(lrecl, end - start));
                recs.add(r);
                start = i + 1;
            }
        }
        return recs;
    }

    static void write(Path p, List<byte[]> recs) throws IOException {
        try (var os = new java.io.BufferedOutputStream(Files.newOutputStream(p))) {
            for (byte[] r : recs) os.write(r);
        }
    }
}
