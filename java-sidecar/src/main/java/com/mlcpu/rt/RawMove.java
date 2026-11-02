package com.mlcpu.rt;

/**
 * A raw (byte-copy) MOVE whose sender and receiver may share storage: its geometry, whether the copy order can change the
 * result, and the policy applied to it.
 *
 * <p>The policy is FRONTIER by default: a {@link Overlap#SHIFTED} move stops at a frontier before any byte is written,
 * because the result depends on the target compiler's copy order (GnuCOBOL -std=ibm copies left to right byte by byte
 * for some shapes and through a snapshot for others; IBM is not captured). A copy order is applied only when an explicit
 * {@link RawMoveProfile} selected by {@link CobConfig#RAW_MOVE_PROFILE} covers the move's exact shape with captured
 * evidence. {@link Overlap#IDENTICAL} and {@link Overlap#ORDER_INDEPENDENT} moves have one result under every order.
 */
public final class RawMove {
    private RawMove() {}

    public enum Overlap {
        /** The sender and receiver byte ranges do not intersect. */
        DISJOINT,
        /** The bytes copied land on themselves (same start, or aligned ends when JUSTIFIED); padding misses every byte read. */
        IDENTICAL,
        /** The ranges intersect, but no byte read from another position is written (copied or padded) by the move. */
        ORDER_INDEPENDENT,
        /** A byte the move reads from another position is also written by it: the result depends on the copy order. */
        SHIFTED
    }

    /** The operand shapes a profile can cover (a reference-modified item is ALNUM). */
    public enum Kind { ALNUM, GROUP, UNSIGNED_DISPLAY_INTEGER, OTHER }

    /**
     * Positions are absolute indexes into one buffer. {@code right}: the receiver is JUSTIFIED RIGHT (the sender's last
     * bytes are kept and padding goes on the left).
     */
    public record Geometry(int senderPos, int senderLen, int receiverPos, int receiverLen, boolean right) {
        public int copied() { return Math.min(senderLen, receiverLen); }
        /** The first sender byte that is copied. */
        public int source() { return senderPos + (right ? senderLen - copied() : 0); }
        /** Where that byte lands. */
        public int target() { return receiverPos + (right ? receiverLen - copied() : 0); }
        public int shift() { return target() - source(); }
        public boolean padded() { return receiverLen > senderLen; }
        public int padPos() { return right ? receiverPos : receiverPos + copied(); }
        public int padLen() { return receiverLen - copied(); }

        public Overlap overlap() {
            if (!intersects(senderPos, senderLen, receiverPos, receiverLen)) return Overlap.DISJOINT;
            if (shift() == 0) return Overlap.IDENTICAL;
            int c = copied();
            boolean conflict = intersects(source(), c, target(), c) || intersects(source(), c, padPos(), padLen());
            return conflict ? Overlap.SHIFTED : Overlap.ORDER_INDEPENDENT;
        }

        @Override public String toString() {
            return "sender@" + senderPos + "+" + senderLen + " receiver@" + receiverPos + "+" + receiverLen
                    + (right ? " JUSTIFIED" : "") + " copied=" + copied() + " shift=" + shift() + (padded() ? " padded=" + padLen() : "");
        }
    }

    static boolean intersects(int a, int alen, int b, int blen) {
        return alen > 0 && blen > 0 && a < b + blen && b < a + alen;
    }

    /** A copy order a profile may apply (only orders with captured evidence exist). */
    public enum Order {
        /** for i in 0..copied-1: buf[target+i] = buf[source+i] (GnuCOBOL cob_move_ibm). */
        LEFT_TO_RIGHT_BYTEWISE;

        void apply(byte[] buf, Geometry g) {
            if (g.right() || g.padded()) throw new IllegalStateException(this + " is defined for left-aligned, unpadded moves only");
            int s = g.source(), t = g.target();
            for (int i = 0, n = g.copied(); i < n; i++) buf[t + i] = buf[s + i];
        }
    }

    /** The sender's shape for the profile. Numeric senders reach here only when their bytes are copied raw. */
    static Kind senderKind(CobField f) {
        if (f.isGroup()) return Kind.GROUP;
        if (f.cat == CobField.Cat.ALNUM) return Kind.ALNUM;
        if (f.cat == CobField.Cat.NUMERIC && f.use == CobField.Use.DISPLAY && !f.signed && f.scale == 0) return Kind.UNSIGNED_DISPLAY_INTEGER;
        return Kind.OTHER;
    }

    static Kind receiverKind(CobField f) {
        if (f.isGroup()) return Kind.GROUP;
        if (f.cat == CobField.Cat.ALNUM) return Kind.ALNUM;
        return Kind.OTHER;
    }

    /**
     * Checks a raw MOVE before any byte is written. Returns false when the caller's ordinary (snapshot) copy is exact; true
     * when the active profile applied the move in place; otherwise stops at a frontier naming {@code site}.
     */
    static boolean apply(CobField sender, CobField receiver, boolean right, String site) {
        if (sender.buf() != receiver.buf()) return false;
        Geometry g = new Geometry(sender.pos(), sender.len, receiver.pos(), receiver.len, right);
        if (g.overlap() != Overlap.SHIFTED) return false;
        String profileId = CobConfig.RAW_MOVE_PROFILE;
        RawMoveProfile profile = profileId.isEmpty() ? null : RawMoveProfile.builtIn(profileId);
        Kind sk = senderKind(sender), rk = receiverKind(receiver);
        RawMoveProfile.Rule rule = profile == null ? null : profile.rule(sk, rk, g);
        if (rule != null) {
            rule.order().apply(sender.buf(), g);
            return true;
        }
        MoveSite at = MoveSite.parse(site);
        String stepId = at != null ? at.stepId() : "runtime:MOVE:" + sender.name + "->" + receiver.name;
        String policy = profileId.isEmpty() ? "default FRONTIER (no raw-move profile selected)"
                : profile == null ? "unknown raw-move profile '" + profileId + "' (built-in: " + RawMoveProfile.builtInIds() + ")"
                : "raw-move profile " + profile.id() + " has no captured rule for this shape";
        CobRt.frontier(stepId, "shifted overlapping raw MOVE" + (at != null ? " at " + at.describe() : "")
                + ": " + name(sender) + " " + sk + " -> " + name(receiver) + " " + rk + " " + g
                + "; copy order is dialect/compiler-dependent; " + policy);
        return false;
    }

    private static String name(CobField f) {
        return f.name == null || f.name.isEmpty() ? "<temp>" : f.name;
    }
}
