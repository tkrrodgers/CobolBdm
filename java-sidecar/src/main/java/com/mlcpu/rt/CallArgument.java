package com.mlcpu.rt;

import java.util.IdentityHashMap;

/** Compatibility CALL ABI: fixed-size shared views, with argument modes separate from storage. */
public final class CallArgument {
    public enum Mode { REFERENCE, CONTENT, OMITTED }
    private final CobField field;
    private final Mode mode;

    private CallArgument(CobField field, Mode mode) {
        this.field = field;
        this.mode = mode;
    }

    public Mode mode() { return mode; }
    public boolean omitted() { return mode == Mode.OMITTED; }
    public CobField field() {
        if (omitted()) throw new CobRt.RuntimeCheck("an omitted CALL argument has no storage");
        return field;
    }
    public byte[] storage() { return field().buf(); }
    public int offset() { return field().pos(); }
    public int length() { return field().len; }

    static CallArgument[] snapshot(CobField[] args) {
        IdentityHashMap<byte[], Area> areas = new IdentityHashMap<>();
        CallArgument[] result = new CallArgument[args.length];
        for (int i = 0; i < args.length; i++) {
            CobField source = args[i];
            if (source == null) {
                result[i] = new CallArgument(null, Mode.OMITTED);
                continue;
            }
            byte[] bytes = source.buf();
            Area area = areas.computeIfAbsent(bytes, b -> {
                Area view = new Area(0);
                view.b = b;
                return view;
            });
            result[i] = new CallArgument(source.over(area, source.pos()),
                    source.isContentArgument() ? Mode.CONTENT : Mode.REFERENCE);
        }
        return result;
    }
}
