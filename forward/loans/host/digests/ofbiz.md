# Rule digest: OFBiz adapters (S8) → the host holes Ledger, Billing and FinancialAccounts, rules OFB and D-11

**Legacy programs:** none. These rules are the SME's: they say how the service's ports land on OFBiz's own accounting (FE section 9.1).

**In the plugin:** everything else is generated from the service model (the services, the entities, the enumerations, the
loan book, the ids and the clock; do not write or change those files). Three classes are yours, one per port the
servicing code calls in OFBiz (`com.mlcpu.loans.api.ports`): `OfbizLedger implements Ledger`, `OfbizBilling implements
Billing`, `OfbizFinancialAccounts implements FinancialAccounts`, in package `org.apache.ofbiz.loans`. Each is a `public final
class` with one public constructor taking the generated `LoansPortContext(dispatcher, delegator, userLogin, today)`.

**How a hole talks to OFBiz:** call a service with `context.dispatcher().runSync(name, map)`, putting `"userLogin"` (the
context's) in the map, and read its result's OUT fields (`paymentId`, `invoiceId`, `finAccountId`, `finAccountTransId`). A
result that `ServiceUtil.isError` or `ServiceUtil.isFailure`, or a `GenericServiceException`, becomes an
`IllegalStateException` with the service name and its message; the calling plugin service then rolls back. Read entities with
`EntityQuery.use(context.delegator()).from(entity).where(field, value, ...).queryOne()` (a `GenericEntityException` becomes an
`IllegalStateException` too). Dates: OFBiz fields are `java.sql.Timestamp`; a business `LocalDate` d is
`Timestamp.valueOf(d.atStartOfDay())`. Only the services and entities of the OFBiz facade digest; no other OFBiz class.

**The loan product:** the plugin's `LoanProduct` entity (key `loanProductId`) carries the product's GL accounts:
`receivableGlAccountId` (the loan receivable; 124000 in the demo) and `interestIncomeGlAccountId` (810000 in the demo). An
unknown product is an `IllegalStateException`.

| Rule | Statement |
|---|---|
| D-11 | `Ledger.postable(currency, organization)`: false when the organization has no `PartyAcctgPreference` or no `baseCurrencyUomId`; true when the currency is that base currency; otherwise true exactly when a `UomConversionDated` row converts the currency to the base currency (`uomId` = currency, `uomIdTo` = base) and is valid on the business date (`context.today()`: `fromDate` ≤ that day and `thruDate` null or after it). OFBiz converts every GL posting to the base currency at the rate valid on its date; a currency without one cannot be posted. |
| OFB-02 | `FinancialAccounts.open(account)`: one `createFinAccount` with `finAccountTypeId` `LOAN_ACCOUNT`, `statusId` `FNACT_ACTIVE`, `ownerPartyId` = the account's owner, `organizationPartyId` = its organization, `currencyUomId` = its currency, and a `finAccountName` naming the loan; returns `finAccountId`. |
| OFB-04 | `FinancialAccounts.post(transaction)`: one `createFinAccountTrans` (never `finAccountWithdraw` or `finAccountDeposit`, whose balance checks refuse a loan's account) with `finAccountId` = the transaction's account, `finAccountTransTypeId` = the type's name (`WITHDRAWAL` or `DEPOSIT`), `amount` = its amount, `transactionDate` = its date, `statusId` `FINACT_TRNS_APPROVED`, `paymentId` = its payment id when it has one, and its invoice id in `comments` when it has one. **No `glAccountId`**: with one, OFBiz posts the transaction to the GL itself (dated now), and the loan would be posted twice. Returns `finAccountTransId`. OFBiz keeps `actualBalance`: a disbursement's WITHDRAWAL makes it −principal, each DEPOSIT adds back. |
| OFB-05 | `Billing.invoice(invoice)`: one `createInvoice` with `invoiceTypeId` `SALES_INVOICE` (OFBiz posts only that type), `partyIdFrom` = the organization (`fromPartyId`), `partyId` = the borrower (`toPartyId`), `currencyUomId`, `invoiceDate` and `dueDate` = the invoice date, `statusId` `INVOICE_IN_PROCESS`, `referenceNumber` = the loan id; then one `createInvoiceItem` per amount above 0, `quantity` 1 and `amount` = the amount: `invoiceItemTypeId` `LOAN_PRINCIPAL` for the principal with `overrideGlAccountId` = the product's receivable, `INV_INTRST_CHRG` for the interest with `overrideGlAccountId` = the product's interest income (no item for an amount of 0); then `setInvoiceStatus` to `INVOICE_READY`, which makes OFBiz post it: Dr accounts receivable (120000), Cr the receivable for the principal, Cr interest income for the interest. Returns `invoiceId`. |
| OFB-06 | `Ledger.disbursementPayment(payment)`: one `createPayment` with `paymentTypeId` `DISBURSEMENT`, `paymentMethodId` = the payment's method, `partyIdFrom` = the organization, `partyIdTo` = the borrower, `amount`, `currencyUomId`, `effectiveDate` = the payment's date, `statusId` `PMNT_NOT_PAID` and `overrideGlAccountId` = the product's receivable; then `setPaymentStatus` to `PMNT_SENT`, which makes OFBiz post it as an outgoing payment: Dr the receivable, Cr the payment method's cash account (111100 in the demo). Returns `paymentId`. |
| OFB-07 | `FinancialAccounts.cancel(id)`: `setFinAccountTransStatus` of that transaction to `FINACT_TRNS_CANCELED`; OFBiz takes its amount back out of `actualBalance`. |

## What the generated code already does (do not repeat it in a hole)

The plugin's services check the permission (OFB-09), map enumeration ids (OFB-03), store schedules at the currency's scale
(OFB-10), and call the servicing use cases, which decide what to invoice, when, and that nothing is billed twice (OFB-08). A
collection is driven by OFBiz: when an invoice becomes PAID (a payment applied, effective on or before now) or leaves PAID (the
payment voided), the plugin's SECA on `setInvoiceStatus` calls `recordLoanInvoiceStatus`, which calls the servicing code, which
calls `FinancialAccounts.post` (a DEPOSIT) or `cancel`. A hole never reads or writes the plugin's own entities (`Loan`,
`LoanScheduleLine`, `LoanServicingLine`) and never changes a status other than the ones above.

## How it is judged

`ofbiz --test component=loans` on a snapshot of OFBiz's demo data plus the plugin's: each scenario calls the plugin's services
(and OFBiz's own payment services, to pay or void an invoice) and then compares the rows: `Loan`, `LoanServicingLine`,
`FinAccount` (with `actualBalance`), `FinAccountTrans`, `Invoice`, `InvoiceItem`, `Payment`, and the GL entries OFBiz posted
(`AcctgTransEntry`: account, debit or credit, amount, currency). OFBiz's checkstyle must report 0 violations (lines at most 150
characters, braces on every block, final classes).
