package com.mlcpu.loans.api.servicing;

/**
 * The loans servicing use cases (rules SRV and IDEM): book a loan, disburse it, bill its installments as they fall due, and
 * collect their principal when its invoices are paid. Through {@code LoansApplication.create(LoansPorts)}; every effect goes
 * through the ports. Each use case is idempotent: repeating a command changes nothing and returns the first outcome.
 */
public interface LoanServicing {

    /** Books a loan: its schedule (version 1) and an account. Idempotent by the command's external id (IDEM-01). */
    BookingOutcome book(BookLoan command);

    /** Disburses a booked loan: the disbursement payment, the account's withdrawal of the principal, the down payment's invoice. */
    DisbursementOutcome disburse(DisburseLoan command);

    /** Bills every installment of an active loan due on or before the date and not billed yet: one invoice each. */
    BillingRun billDue(BillDue command);

    /** A loan invoice became paid, or stopped being paid (a voided payment): its principal is collected, or that collection cancelled. */
    CollectionOutcome invoicePaid(InvoicePaid event);
}
