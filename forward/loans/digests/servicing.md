# Rule digest: servicing (book, disburse, bill, collect) → the application, rules SRV and IDEM

**Legacy programs:** none. The legacy system computed schedules and stopped there; servicing is new (FE section 3.2: the ERP
books a loan, pays it out, bills its installments and collects them). These rules are the SME's.

**In the service:** `LoansApplication.create(LoansPorts)` gives a `LoanServicing` (package `com.mlcpu.loans.api.servicing`).
It computes schedules as `quotes().schedule(...)` does and reaches everything else through the ports (`com.mlcpu.loans.api.ports`):
`Ids` (a loan's id from its external id), `LoanBook` (the loans), `Ledger` (what can be posted; the disbursement payment),
`Billing` (invoices), `FinancialAccounts` (the loan's account and its transactions) and `Clock` (the business date). The
servicing code is in `loans-application`; it keeps no state of its own: each use case reads the loan from the book and saves
its new state. Every command is idempotent: repeating it changes nothing and returns the first outcome.

**A loan's lines to bill** are its down payment line and its installment lines, in the schedule's order: number, due date,
principal and interest (a down payment has interest 0 at the currency's decimal places), then its invoice id and billing date
(null until billed) and the deposit that collected its principal (null until collected). A line **owes** its principal plus
its interest.

| Rule | Statement |
|---|---|
| SRV-01 | Booking: the loan id is `ids.loanId(externalId)`; the schedule is the terms' with that loan id as `loanId`. A rejected schedule gives `BookingRejected(externalId, SCHEDULE_REJECTED, reason, field)` with the schedule's reason and field, and nothing is stored. |
| SRV-02 | Then the ledger's limits (decisions D-11, D-12), in this order, each a `BookingRejected(externalId, reason, null, null)` with nothing stored: the currency's decimal places above 2: `CURRENCY_SCALE_UNSUPPORTED_BY_LEDGER`; `ledger.postable(currency, organizationPartyId)` false: `NO_EXCHANGE_RATE_TO_BASE_CURRENCY`; an annual rate with more than 6 decimal places (trailing zeros do not count): `RATE_PRECISION_UNSUPPORTED_BY_LEDGER`; an amount of the schedule at or above 10^12 (in absolute value; the totals' disbursed, principal, interest and repayment, and every line's amount, total due, balance and remaining due): `AMOUNT_EXCEEDS_LEDGER_CAPACITY`. |
| SRV-03 | A booked loan: first its account is opened (`accounts.open(LoanAccount(loanId, owner, organization, currency))`), then the loan is saved: status `BOOKED`, `bookedOn` = today, schedule version 1, the schedule, its lines to bill, no disbursement yet. The result is `Booked(loanId, accountId, 1)`. |
| SRV-04 | Disbursing a `BOOKED` loan: the disbursement payment (`ledger.disbursementPayment`: from the organization to the owner, the command's payment method, the currency, the disbursement line's amount, on its date), then the account's `WITHDRAWAL` of that amount on that date naming the payment; then, when the down payment line owes more than 0, its invoice (as SRV-06, dated the disbursement date), the line billed on the disbursement date. The loan is saved `ACTIVE` with the payment and the withdrawal. The result is `Disbursed(loanId, paymentId, withdrawalId, downPaymentInvoiceId)` (null without a down payment invoice). |
| SRV-05 | Disbursing a loan id the book does not have: `ServicingRejected(loanId, UNKNOWN_LOAN)`; nothing changes. |
| SRV-06 | Billing as of a date: the `ACTIVE` loans in loan-id order, and in each its lines in order. A line not billed yet, due on or before the date, that owes more than 0, gets an invoice (`billing.invoice(LoanInvoice(loanId, loanProductId, line number, organization, owner, currency, invoice date = the line's due date, principal, interest))`) and is billed as of the run's date. A loan whose lines changed is saved once. The result lists the billed lines in that order (`InstallmentBilled(loanId, number, invoiceId, principal, interest)`). |
| SRV-07 | A line that owes 0 is never billed, and counts as collected for SRV-08. |
| SRV-08 | An invoice paid (`InvoicePaid(invoiceId, true)`) of a line not collected yet: a `DEPOSIT` of the line's principal (not its interest) on the loan's account, dated today, naming the invoice; the line is collected by it. When every line that owes more than 0 is collected, the loan is `PAID_OFF`. The result is `PrincipalCollected(loanId, number, depositId, paidOff)`. |
| SRV-09 | An invoice no longer paid (`InvoicePaid(invoiceId, false)`) of a collected line: its deposit is cancelled (`accounts.cancel`), the line is no longer collected, and the loan is `ACTIVE` (also when it was `PAID_OFF`). The result is `CollectionCancelled(loanId, number, depositId)` with the cancelled deposit. |
| SRV-10 | An invoice that is no loan line's: `ServicingRejected(invoiceId, UNKNOWN_INVOICE)`; nothing changes. |
| IDEM-01 | Booking a loan id the book already has returns that loan's `Booked(loanId, accountId, scheduleVersion)` and changes nothing, whatever the command's terms. A rejected booking stored nothing, so booking again is decided afresh. |
| IDEM-02 | Disbursing a loan that is not `BOOKED` (it was disbursed) returns its `Disbursed` from the book (its payment, its withdrawal, its down payment line's invoice) and changes nothing. |
| IDEM-03 | A billed line is never billed again: a billing run with nothing new to bill returns an empty list and changes nothing. |
| IDEM-04 | An invoice paid of a line already collected, or no longer paid of a line not collected: `CollectionUnchanged(loanId, number)`; nothing changes. |

## Order of the port calls

Within a use case, in the order the rules state them: booking opens the account, then saves the loan; disbursement creates
the payment, then the withdrawal, then the down payment invoice, then saves the loan; billing invoices line by line and saves a
loan after its lines; collection posts or cancels the transaction, then saves the loan. (The in-memory ports number their ids in
the order of the calls.)

## Not carried forward

There is no legacy servicing. Repayment allocation, prepayment, payoff, rescheduling, rate changes, fees and penalties come with
the event-driven calculator (wave 2).
