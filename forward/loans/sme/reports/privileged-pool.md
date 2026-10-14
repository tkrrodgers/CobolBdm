# Privileged schedule scenarios (decision D-6): verification and selection

Built by `PrivilegedPool` from `pool/privileged` (requests: `tools/make_privileged.py`; expected values: oracle A' D-2 without the legacy envelope, `FnHarness --forward-limits`; `tools/build_schedule_pool.py LABEL=...`). The judge counts them apart (privileged); no model sees them.

| Axis | Read | Reference equal | Scheduled | Rejected | Chosen |
| --- | --- | --- | --- | --- | --- |
| combined | 20 | 20 | 20 | 0 | 5 |
| envelope | 6 | 6 | 6 | 0 | 3 |
| every | 120 | 120 | 68 | 52 | 30 |
| long | 32 | 32 | 28 | 4 | 13 |
| precision | 360 | 360 | 325 | 35 | 43 |
| principal | 114 | 114 | 98 | 16 | 21 |
| **all** | 652 | 652 | | | 115 |

Chosen by outcome: Rejected DISBURSEMENT_OUTSIDE_TERM 8; Rejected DOWN_PAYMENT_COVERS_PRINCIPAL 2; Rejected PERIOD_NOT_SUPPORTED_WITH_THIRTY_DAY_MONTHS 7; Rejected SINGLE_INSTALLMENT_AFTER_LATE_DISBURSEMENT 21; Scheduled 77; 
