package com.mlcpu.sidecar;

import java.util.List;
import java.util.Map;

/** Gson mirror of IR v1.2 (§7.1) with the semantic tree (mlcpu.semantic-tree.v1). */
public final class Ir {
    public static final String SUPPORTED_VERSION = "1.2";

    public static boolean supports(String v) {
        return "1.1".equals(v) || "1.2".equals(v);
    }

    public String irVersion;
    public Program program;
    public List<Field> dictionary;
    public List<Paragraph> paragraphs;
    public List<Frontier> frontiers;
    public List<Name> naming;
    public List<Record> records;
    public Semantics semantics;
    public String semanticSchema;

    public static final class Program {
        public String id;
        public String path;
        public boolean execSql;
        public boolean execCics;
        public Map<String, String> fileStatus;
        public Map<String, String> fileRecord;
        public List<String> using;
        public Map<String, String> recordFile;
        public List<File> files;
    }

    public static final class File {
        public String name;
        public String assign;
        public String organization;
        public String access;
        public Integer recordKey;
        public List<AltKey> alternateKeys;
        public Integer fileStatus;
        public String recordingMode;
        public int minRecord;
        public int maxRecord;
        public Integer recordDependingOn;
        public List<Integer> records;
        public boolean sort;
    }

    public static final class AltKey {
        public int key;
        public boolean duplicates;
    }

    public static final class Value {
        public String v;
        public boolean q;
        public String hi;
        public Boolean hq;
    }

    public static final class Field {
        public int id;
        public String field;
        public String level;
        public String pic;
        public String usage;
        public String occurs;
        public String redefines;
        public boolean signed;
        public int impliedDecimals;
        public String type;
        public String notes;
        public List<Integer> dims;
        public String occursOwner;
        public Integer parentId;
        public int recordId;
        public String section;
        public int offset;
        public int length;
        public List<Integer> strides;
        public String category;
        public int digits;
        public boolean signSeparate;
        public boolean signLeading;
        public boolean justified;
        public boolean blankWhenZero;
        public List<Value> values;
        public boolean valueAll;
        public String file;
        public Integer redefinesId;
        public String dependingOn;
        public int line;

        public int lvl() { return Integer.parseInt(level); }
    }

    public static final class Paragraph {
        public String name;
        public int start;
        public int end;
        public List<Statement> statements;
    }

    public static final class Guard {
        public String predicateStepId;
        public String branch;
    }

    public static final class Statement {
        public String stepId;
        public int line;
        public String opcode;
        public String text;
        public List<String> defs;
        public List<String> uses;
        public List<Guard> guards;
        public String performTarget;
        public String performThru;
        public String goToTarget;
        public boolean isExit;
        public boolean endsSentence;
        public String semanticNodeId;
        public String semanticStatus;
    }

    public static final class Frontier {
        public String kind;
        public String subject;
        public String detail;
    }

    public static final class Name {
        public String original;
        public String modern;
    }

    public static final class Record {
        public String cobol;
        public int length;
        public boolean placeable;
    }

    public static final class Semantics {
        public String schema;
        public List<SemParagraph> paragraphs;
    }

    public static final class SemParagraph {
        public String name;
        public String section;
        public boolean isSection;
        public int line;
        public List<Stmt> statements;
    }

    public static final class Node {
        public String k;
        public String v;
        public String op;
        public Integer id;
        public Node l;
        public Node r;
        public List<Node> args;
        public Node start;
        public Node len;
        public Boolean rounded;
    }

    public static final class Stmt {
        public String verb;
        public String stepId;
        public int line;
        public int sentence;
        public String text;
        public String frontier;
        public Map<String, Node> n;
        public Map<String, List<Node>> l;
        public Map<String, String> s;
        public Map<String, List<Stmt>> blocks;

        public Node n(String k) { return n == null ? null : n.get(k); }
        public List<Node> l(String k) { return l == null || l.get(k) == null ? List.of() : l.get(k); }
        public String s(String k) { return s == null ? null : s.get(k); }
        public List<Stmt> block(String k) { return blocks == null ? null : blocks.get(k); }
    }
}
