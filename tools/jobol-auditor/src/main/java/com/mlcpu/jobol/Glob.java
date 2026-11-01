package com.mlcpu.jobol;

import java.util.regex.Pattern;

/** Path globs over '/'-separated relative paths: '**' crosses directories, '**&#47;' also matches none, '*' and '?' do not cross '/'. */
public final class Glob {
    private Glob() {
    }

    public static Pattern compile(String glob) {
        StringBuilder sb = new StringBuilder("^");
        for (int i = 0; i < glob.length(); i++) {
            char c = glob.charAt(i);
            if (c == '*') {
                if (i + 1 < glob.length() && glob.charAt(i + 1) == '*') {
                    if (i + 2 < glob.length() && glob.charAt(i + 2) == '/') {
                        sb.append("(?:.*/)?");
                        i += 2;
                    } else {
                        sb.append(".*");
                        i += 1;
                    }
                } else {
                    sb.append("[^/]*");
                }
            } else if (c == '?') {
                sb.append("[^/]");
            } else if ("\\.[]{}()+-^$|".indexOf(c) >= 0) {
                sb.append('\\').append(c);
            } else {
                sb.append(c);
            }
        }
        return Pattern.compile(sb.append('$').toString());
    }
}
