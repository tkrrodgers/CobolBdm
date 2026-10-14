package com.mlcpu.loans.acceptance;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.node.ArrayNode;
import com.fasterxml.jackson.databind.node.ObjectNode;
import com.mlcpu.loans.acceptance.memory.InMemoryPorts;
import com.mlcpu.loans.api.Calculation;
import com.mlcpu.loans.api.ScheduleRequest;
import com.mlcpu.loans.api.ScheduleService;
import com.mlcpu.loans.api.ports.LoansPorts;
import com.mlcpu.loans.api.servicing.BillDue;
import com.mlcpu.loans.api.servicing.BookLoan;
import com.mlcpu.loans.api.servicing.DisburseLoan;
import com.mlcpu.loans.api.servicing.InvoicePaid;
import com.mlcpu.loans.api.servicing.LoanServicing;
import com.mlcpu.loans.api.spi.DueDates;
import com.mlcpu.loans.api.spi.RateFactors;
import com.mlcpu.loans.api.spi.RateQuery;
import java.lang.reflect.InvocationTargetException;
import java.time.LocalDate;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;

/**
 * One acceptance scenario (JSON): steps, each a call with its input and its expected result (or the exception it must throw).
 * {@link #run} executes it through the entry points of {@code com.mlcpu.loans.application.LoansApplication} and returns the
 * differences. The entry points are reached by name, so a slice's scenarios run before later slices add theirs:
 * <ul>
 * <li>{@code periods}: {@code LoansApplication.dueDates().periods(ScheduleRequest)};</li>
 * <li>{@code factors}: {@code LoansApplication.rateFactors().factors(query, calculation)}, input {"query", "calculation"};</li>
 * <li>{@code schedule}, {@code document}, {@code disclose}: {@code LoansApplication.quotes()}, the ScheduleService method of that name;</li>
 * <li>{@code book}, {@code disburse}, {@code billDue}, {@code invoicePaid}: {@code LoansApplication.create(LoansPorts)}, the LoanServicing
 * method of that name, over in-memory ports made from the scenario's "given" ({@link InMemoryPorts}); {@code today} sets their clock.
 * After the steps, a scenario with a "state" compares the ports' state with it.</li>
 * </ul>
 */
public record Scenario(String id, String slice, String visibility, JsonNode json) {

    static final String ENTRY_CLASS = "com.mlcpu.loans.application.LoansApplication";

    /** An entry point of LoansApplication: a public static method without parameters. */
    static Object entry(String method) throws ReflectiveOperationException {
        Class<?> app = Class.forName(ENTRY_CLASS);
        return app.getMethod(method).invoke(null);
    }

    /** What a step produced: its canonical result, or the exception it threw. */
    public static JsonNode execute(String call, JsonNode input) {
        try {
            Object result = switch (call) {
                case "periods" -> ((DueDates) entry("dueDates")).periods(Json.to(input, ScheduleRequest.class));
                case "factors" -> ((RateFactors) entry("rateFactors")).factors(Json.to(input.get("query"), RateQuery.class),
                        Json.to(input.get("calculation"), Calculation.class));
                case "schedule" -> ((ScheduleService) entry("quotes")).schedule(Json.to(input, ScheduleRequest.class));
                case "document" -> ((ScheduleService) entry("quotes")).document(Json.to(input, ScheduleRequest.class));
                case "disclose" -> ((ScheduleService) entry("quotes")).disclose(Json.to(input, ScheduleRequest.class));
                default -> throw new IllegalArgumentException("unknown call " + call);
            };
            return Json.of(result);
        } catch (NoSuchMethodException | ClassNotFoundException e) {
            return thrown(e.getClass().getSimpleName(), "the entry point is missing: " + ENTRY_CLASS + " needs " + e.getMessage(), null);
        } catch (InvocationTargetException e) {
            return thrown(e.getCause());
        } catch (ReflectiveOperationException e) {
            return thrown(e);
        } catch (RuntimeException | StackOverflowError e) {
            return thrown(e);
        }
    }

    private static JsonNode thrown(Throwable e) {
        StackTraceElement at = Arrays.stream(e.getStackTrace()).filter(f -> f.getClassName().startsWith("com.mlcpu.loans.")
                && !f.getClassName().startsWith("com.mlcpu.loans.acceptance")).findFirst().orElse(null);
        return thrown(e.getClass().getSimpleName(), String.valueOf(e.getMessage()),
                at == null ? null : at.getClassName() + "." + at.getMethodName() + "(" + at.getFileName() + ":" + at.getLineNumber() + ")");
    }

    private static JsonNode thrown(String type, String message, String at) {
        ObjectNode o = Json.F.objectNode();
        ObjectNode t = o.putObject("throws");
        t.put("type", type);
        t.put("message", message);
        if (at != null) t.put("at", at);
        return o;
    }

    /** The servicing entry point over the given ports: {@code LoansApplication.create(LoansPorts)}. */
    static LoanServicing servicing(LoansPorts ports) throws ReflectiveOperationException {
        return (LoanServicing) Class.forName(ENTRY_CLASS).getMethod("create", LoansPorts.class).invoke(null, ports);
    }

    /** A servicing step's result, or the exception it threw. */
    public static JsonNode execute(InMemoryPorts memory, String call, JsonNode input) {
        try {
            if (call.equals("today")) {
                memory.today(LocalDate.parse(input.asText()));
                return input;
            }
            LoanServicing s = servicing(memory.ports());
            Object result = switch (call) {
                case "book" -> s.book(Json.to(input, BookLoan.class));
                case "disburse" -> s.disburse(Json.to(input, DisburseLoan.class));
                case "billDue" -> s.billDue(Json.to(input, BillDue.class));
                case "invoicePaid" -> s.invoicePaid(Json.to(input, InvoicePaid.class));
                default -> throw new IllegalArgumentException("unknown call " + call);
            };
            return Json.of(result);
        } catch (NoSuchMethodException | ClassNotFoundException e) {
            return thrown(e.getClass().getSimpleName(), "the entry point is missing: " + ENTRY_CLASS + " needs " + e.getMessage(), null);
        } catch (InvocationTargetException e) {
            return thrown(e.getCause());
        } catch (ReflectiveOperationException e) {
            return thrown(e);
        } catch (RuntimeException | StackOverflowError e) {
            return thrown(e);
        }
    }

    static final List<String> SERVICING = List.of("today", "book", "disburse", "billDue", "invoicePaid");

    /** Runs the scenario; returns the first differences (empty when it passes). */
    public List<String> run(int limit) {
        List<String> out = new ArrayList<>();
        ArrayNode steps = (ArrayNode) json.path("steps");
        InMemoryPorts memory = json.has("given") ? new InMemoryPorts(Json.to(json.get("given"), InMemoryPorts.Given.class)) : null;
        for (int i = 0; i < steps.size() && out.size() < limit; i++) {
            JsonNode step = steps.get(i);
            String call = step.path("call").asText();
            JsonNode actual = memory != null && SERVICING.contains(call) ? execute(memory, call, step.path("input")) : execute(call, step.path("input"));
            out.addAll(Json.differences("step " + (i + 1) + " " + call, step.path("expected"), actual, limit - out.size()));
        }
        if (memory != null && json.has("state") && out.size() < limit) {
            out.addAll(Json.differences("state", json.get("state"), Json.of(memory.state()), limit - out.size()));
        }
        return out;
    }
}
