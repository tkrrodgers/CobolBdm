package com.mlcpu.sidecar;

import static org.junit.jupiter.api.Assertions.*;

import com.mlcpu.rt.Area;
import com.mlcpu.rt.CobConfig;
import com.mlcpu.rt.CobField;
import com.mlcpu.rt.CobFile;
import com.mlcpu.rt.CobRt;
import com.mlcpu.rt.CobolBase;
import com.mlcpu.rt.ProgramRegistry;
import java.lang.reflect.Field;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;

class StorageCompositionTest {
    private final byte savedDefault = CobConfig.DEFAULT_BYTE;

    @AfterEach
    void reset() {
        CobConfig.DEFAULT_BYTE = savedDefault;
        CobRt.resetRunUnit();
        ProgramRegistry.reset();
    }

    @Test
    void composedStorageOwnsAreasAndViewsButNotExecutionOrFiles() throws Exception {
        Ir ir = program();
        Ir.File file = new Ir.File();
        file.name = "OUT-FILE";
        file.assign = "OUT";
        file.organization = "SEQUENTIAL";
        file.access = "SEQUENTIAL";
        file.recordingMode = "F";
        file.minRecord = file.maxRecord = 8;
        file.records = List.of(10);
        file.fileStatus = 11;
        ir.program.files = List.of(file);
        Ir.Field record = field(10, "OUT-REC", "Group", 8, "File", null, 10);
        record.file = file.name;
        ir.dictionary.add(record);
        ir.dictionary.add(field(11, "FILE-STATUS", "Alphanumeric", 2, "WorkingStorage", null, 11));
        file.sort = true;
        Ir.Node key = node("key", null);
        key.op = "ASC";
        key.l = ref(10);
        Ir.Stmt sort = stmt("SORT", "SORT:1");
        sort.s = Map.of("file", file.name);
        sort.l = Map.of("keys", List.of(key));
        ir.semantics.paragraphs.get(0).statements = List.of(sort, stmt("GOBACK", "SORT:2"));
        SkeletonEmitter.Emission emission = new SkeletonEmitter().emit(ir, true);
        CobolBase p = compile(emission);
        Object storage = storage(p);
        assertEquals(Object.class, storage.getClass().getSuperclass());
        assertEquals(0, List.of(storage.getClass().getDeclaredMethods()).stream()
                .filter(m -> m.getName().startsWith("p") || m.getName().equals("call")).count());
        assertTrue(List.of(p.getClass().getDeclaredFields()).stream().noneMatch(f -> f.getType() == CobField.class || f.getType() == Area.class));
        assertTrue(List.of(p.getClass().getDeclaredFields()).stream().anyMatch(f -> f.getType() == CobFile.class));
        assertTrue(List.of(storage.getClass().getDeclaredFields()).stream().noneMatch(f -> f.getType() == CobFile.class));
        assertEquals(emission.fieldCount, new TreeApiChecker().scan(p.getClass(), true).fields.size());
        assertTrue(emission.source.contains("\"OUT-FILE\", _storage.aF_OUT_FILE"));
    }

    @Test
    void aliasesLinkageLocalStorageAndRetainedWorkingStorageMatchFaithfulBytes() throws Exception {
        for (boolean initial : List.of(false, true)) {
            Ir ir = program();
            ir.program.initial = initial;
            List<List<String>> outcomes = new ArrayList<>();
            for (boolean composed : List.of(false, true)) {
                CobRt.resetRunUnit();
                CobConfig.DEFAULT_BYTE = 0;
                CobolBase p = compile(new SkeletonEmitter().emit(ir, composed));
                List<String> snapshots = new ArrayList<>();
                for (int invocation = 0; invocation < 2; invocation++) {
                    byte[] argument = "--------".getBytes(StandardCharsets.ISO_8859_1);
                    p.call(new CobField[]{CobField.temp(argument)}, true);
                    CobField text = view(p, "text");
                    CobField prefix = view(p, "prefix");
                    assertSame(text.area, prefix.area);
                    assertSame(text.buf(), prefix.buf());
                    assertEquals("WXYZEFGH", text.str());
                    assertEquals(5, view(p, "localCount").intValue());
                    assertEquals(6, view(p, "localLast").intValue());
                    assertEquals(initial ? 2 : 2 + invocation, view(p, "counter").intValue());
                    assertSame(argument, view(p, "argText").buf());
                    assertEquals(initial || invocation == 0 ? "WXYZEFGH" : "--------",
                            new String(argument, StandardCharsets.ISO_8859_1));
                    snapshots.add(java.util.HexFormat.of().formatHex(text.bytes()) + ":"
                            + java.util.HexFormat.of().formatHex(view(p, "counter").bytes()) + ":"
                            + java.util.HexFormat.of().formatHex(view(p, "localLast").bytes()) + ":"
                            + java.util.HexFormat.of().formatHex(argument));
                }
                outcomes.add(snapshots);
            }
            assertEquals(outcomes.get(0), outcomes.get(1));
        }
    }

    @Test
    void dynamicCallStillResolvesAtRuntimeAndWritesCallerLinkage() throws Exception {
        Ir ir = program();
        Ir.Field target = field(12, "TARGET", "Alphanumeric", 8, "WorkingStorage", null, 12);
        target.values = List.of(value("SUB", true));
        ir.dictionary.add(target);
        Ir.Node argument = node("arg", null);
        argument.op = "REFERENCE";
        argument.l = ref(8);
        Ir.Stmt call = stmt("CALL", "CALL:1");
        call.n = Map.of("target", ref(12));
        call.l = Map.of("args", List.of(argument));
        ir.semantics.paragraphs.get(0).statements = List.of(call, stmt("GOBACK", "CALL:2"));
        CobolBase p = compile(new SkeletonEmitter().emit(ir, true));
        ProgramRegistry.registerModern("SUB", parameters -> parameters[0][0] = 'S');
        byte[] bytes = "--------".getBytes(StandardCharsets.ISO_8859_1);
        p.call(new CobField[]{CobField.temp(bytes)}, true);
        assertEquals("S-------", new String(bytes, StandardCharsets.ISO_8859_1));
        CobRt.move("OTHER", view(p, "target"));
        ProgramRegistry.registerModern("OTHER", parameters -> parameters[0][0] = 'O');
        p.call(new CobField[]{CobField.temp(bytes)}, true);
        assertEquals("O-------", new String(bytes, StandardCharsets.ISO_8859_1));
        CobRt.move("MISSING", view(p, "target"));
        assertThrows(CobRt.CallNotFound.class, () -> p.call(new CobField[]{CobField.temp(bytes)}, true));
    }

    @Test
    void largeStorageAndInitializationHelpersCompileAndPreserveEveryValue() throws Exception {
        Ir ir = program();
        for (int id = 100; id < 1201; id++) {
            Ir.Field item = field(id, "ITEM-" + id, "Numeric", 3, "WorkingStorage", null, id);
            item.values = List.of(value("7", false));
            ir.dictionary.add(item);
        }
        SkeletonEmitter.Emission emission = new SkeletonEmitter().emit(ir, true);
        assertTrue(emission.source.contains("_items1()"));
        assertTrue(emission.source.contains("_init1()"));
        assertTrue(emission.source.contains("_fields1("));
        CobolBase p = compile(emission);
        p.ensureInit();
        for (int id = 100; id < 1201; id++) assertEquals(7, view(p, "item" + id).intValue());
    }

    @Test
    void compositionIsDeterministicAndDoesNotChangeTheDefaultEmission() {
        Ir ir = program();
        SkeletonEmitter emitter = new SkeletonEmitter();
        String faithful = emitter.emit(ir).source;
        String composed = emitter.emit(ir, true).source;
        assertEquals(composed, emitter.emit(ir, true).source);
        assertEquals(faithful, emitter.emit(ir).source);
        assertFalse(faithful.contains("private final Storage _storage"));
    }

    @Test
    void storageMemberNameIsReservedOnlyInTheComposedLane() throws Exception {
        Ir ir = program();
        Ir.Name name = new Ir.Name();
        name.original = "TEXT";
        name.modern = "_storage";
        ir.naming = List.of(name);
        CobolBase faithful = compile(new SkeletonEmitter().emit(ir));
        faithful.ensureInit();
        Field field = faithful.getClass().getDeclaredField("_storage");
        field.setAccessible(true);
        assertEquals("ABCDEFGH", ((CobField) field.get(faithful)).str());
        CobolBase composed = compile(new SkeletonEmitter().emit(ir, true));
        composed.ensureInit();
        assertEquals("ABCDEFGH", view(composed, "_storage_").str());
    }

    @Test
    void protocolReturnsCompiledCompositionAndASeparateFaithfulBaseline() throws Exception {
        com.google.gson.Gson gson = new com.google.gson.Gson();
        com.google.gson.JsonObject request = new com.google.gson.JsonObject();
        request.add("ir", gson.toJsonTree(program()));
        request.addProperty("composeStorage", true);
        request.addProperty("transpileOnly", true);
        Map<?, ?> response = assertInstanceOf(Map.class, Sidecar.transpileVerify(gson.toJson(request)));
        assertEquals(true, response.get("compiled"), String.valueOf(response.get("diagnostics")));
        assertEquals(true, response.get("storageComposition"));
        assertEquals("TRANSPILED", response.get("verdict"));
        assertTrue(String.valueOf(response.get("javaSource")).contains("private final Storage _storage"));
        assertFalse(String.valueOf(response.get("faithfulJavaSource")).contains("private final Storage _storage"));
        assertTrue(String.valueOf(response.get("faithfulJavaSource")).contains("final Area "));
    }

    private static CobolBase compile(SkeletonEmitter.Emission emission) throws Exception {
        JavaCompilerService.Result result = new JavaCompilerService().compile(emission.className, emission.source);
        assertTrue(result.compiled, String.join("\n", result.diagnostics));
        ClassLoader loader = new JavaCompilerService.MemoryLoader(result.classes, StorageCompositionTest.class.getClassLoader());
        return (CobolBase) Class.forName(emission.className, true, loader).getDeclaredConstructor().newInstance();
    }

    private static Object storage(CobolBase p) throws Exception {
        Field field = p.getClass().getDeclaredField("_storage");
        field.setAccessible(true);
        return field.get(p);
    }

    private static CobField view(CobolBase p, String name) throws Exception {
        Object owner = List.of(p.getClass().getDeclaredFields()).stream().anyMatch(f -> f.getName().equals("_storage")) ? storage(p) : p;
        Field field = owner.getClass().getDeclaredField(name);
        field.setAccessible(true);
        return (CobField) field.get(owner);
    }

    private static Ir program() {
        Ir ir = new Ir();
        ir.irVersion = Ir.SUPPORTED_VERSION;
        ir.program = new Ir.Program();
        ir.program.id = "COMPOSE";
        ir.program.path = "COMPOSE.cbl";
        ir.program.using = List.of("ARG");
        ir.program.files = List.of();
        ir.dictionary = new ArrayList<>();
        ir.dictionary.add(field(1, "WORK-REC", "Group", 8, "WorkingStorage", null, 1));
        Ir.Field text = field(2, "TEXT", "Alphanumeric", 8, "WorkingStorage", 1, 1);
        text.values = List.of(value("ABCDEFGH", true));
        ir.dictionary.add(text);
        Ir.Field overlay = field(3, "OVERLAY", "Group", 8, "WorkingStorage", null, 3);
        overlay.redefines = "WORK-REC";
        overlay.redefinesId = 1;
        ir.dictionary.add(overlay);
        ir.dictionary.add(field(4, "PREFIX", "Alphanumeric", 4, "WorkingStorage", 3, 3));
        Ir.Field counter = field(5, "COUNTER", "Numeric", 3, "WorkingStorage", null, 5);
        counter.values = List.of(value("1", false));
        ir.dictionary.add(counter);
        Ir.Field local = field(6, "LOCAL-COUNT", "Numeric", 3, "LocalStorage", null, 6);
        local.values = List.of(value("5", false));
        ir.dictionary.add(local);
        ir.dictionary.add(field(13, "LOCAL-LAST", "Numeric", 3, "WorkingStorage", null, 13));
        ir.dictionary.add(field(7, "ARG", "Group", 8, "Linkage", null, 7));
        ir.dictionary.add(field(8, "ARG-TEXT", "Alphanumeric", 8, "Linkage", 7, 7));
        Ir.Field ready = field(9, "READY", "Numeric", 0, "WorkingStorage", 5, 5);
        ready.level = "88";
        ready.values = List.of(value("2", false));
        ir.dictionary.add(ready);
        Ir.Stmt branch = stmt("IF", "MAIN:4");
        Ir.Node condition = ref(9);
        condition.k = "c88";
        branch.n = Map.of("cond", condition);
        branch.blocks = Map.of("TRUE", List.of(move(ref(2), ref(8), "MAIN:5")));
        Ir.SemParagraph paragraph = new Ir.SemParagraph();
        paragraph.name = "MAIN";
        paragraph.statements = List.of(move(node("str", "WXYZ"), ref(4), "MAIN:1"),
                add(ref(5), "MAIN:2"), add(ref(6), "MAIN:3"), move(ref(6), ref(13), "MAIN:3A"),
                branch, stmt("GOBACK", "MAIN:6"));
        ir.semantics = new Ir.Semantics();
        ir.semantics.schema = "mlcpu.semantic-tree.v1";
        ir.semantics.paragraphs = List.of(paragraph);
        return ir;
    }

    private static Ir.Field field(int id, String name, String category, int length, String section, Integer parent, int record) {
        Ir.Field field = new Ir.Field();
        field.id = id;
        field.field = name;
        field.level = parent == null ? "01" : "05";
        field.category = category;
        field.length = length;
        field.pic = category.equals("Numeric") ? "9(" + length + ")" : "X(" + length + ")";
        field.usage = "DISPLAY";
        field.section = section;
        field.parentId = parent;
        field.recordId = record;
        field.digits = category.equals("Numeric") ? length : 0;
        return field;
    }

    private static Ir.Value value(String text, boolean quoted) {
        Ir.Value value = new Ir.Value();
        value.v = text;
        value.q = quoted;
        return value;
    }

    private static Ir.Node node(String kind, String value) {
        Ir.Node node = new Ir.Node();
        node.k = kind;
        node.v = value;
        return node;
    }

    private static Ir.Node ref(int id) {
        Ir.Node node = node("ref", null);
        node.id = id;
        return node;
    }

    private static Ir.Stmt stmt(String verb, String id) {
        Ir.Stmt statement = new Ir.Stmt();
        statement.verb = verb;
        statement.stepId = id;
        statement.text = verb;
        return statement;
    }

    private static Ir.Stmt move(Ir.Node from, Ir.Node to, String id) {
        Ir.Stmt statement = stmt("MOVE", id);
        statement.n = Map.of("src", from);
        statement.l = Map.of("dst", List.of(to));
        return statement;
    }

    private static Ir.Stmt add(Ir.Node to, String id) {
        Ir.Stmt statement = stmt("ADD", id);
        statement.s = Map.of("form", "TO");
        statement.l = Map.of("ops", List.of(node("num", "1")), "dst", List.of(to));
        return statement;
    }
}
