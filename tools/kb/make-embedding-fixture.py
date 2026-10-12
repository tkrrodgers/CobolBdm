r"""Reference vectors for `kb verify-model`: EmbeddingGemma through the Hugging Face tokenizer (tokenizer.json) and
Python onnxruntime, independent of the .NET path (Microsoft.ML.Tokenizers + ML.NET ApplyOnnxModel).

  python -I tools/kb/make-embedding-fixture.py C:\models\embeddinggemma-300m-ONNX knowledge/fixtures/embeddinggemma-reference.json
Needs: tokenizers, onnxruntime, numpy (a throwaway venv is enough)."""
import json, sys, numpy as np, onnxruntime as ort, tokenizers
from tokenizers import Tokenizer

model_dir, out = sys.argv[1], sys.argv[2]
texts = [
    "task: search result | query: How is monthly interest calculated for a credit card account?",
    "task: search result | query: rounding mode HALF_EVEN banker's rounding of the installment",
    "title: FNEM0300 · paragraph 1100-STEP · Rate factors of interest periods | text: MOVE LM-IP-FROM-DAY ( WS-K WS-J ) TO WS-IP-FROM\n  MOVE LM-IP-DUE-DAY ( WS-K WS-J ) TO WS-IP-DUE\n  SET WS-PER-PERIOD TO TRUE\n  PERFORM 2000-RATE-FACTOR THRU 2000-RATE-FACTOR-EXIT",
    "title: CBACT04C · rule R-CBACT04C-006 | text: COMPUTE WS-MONTHLY-INT = ( TRAN-CAT-BAL * DIS-INT-RATE) / 1200",
    "title: CBTRN02C · Java Prog_CBTRN02C · paragraph 1500-B-LOOKUP-ACCT | text:     // [1500-B-LOOKUP-ACCT:3:COMPUTE:L403] COMPUTE WS-TEMP-BAL = ACCT-CURR-CYC-CREDIT - ACCT-CURR-CYC-DEBIT + DALYTRAN-AMT\n        CobRt.store(tempBal, CobRt.add(CobRt.sub(acctCurrCycCredit.dec(), acctCurrCycDebit.dec()), dalytranAmt.dec()), false, false);",
    "title: SAM1 · program | text: READS A SEQUENTIAL TRANSACTION FILE AND MAKES UPDATES TO A SORTED SEQUENTIAL CUSTOMER FILE",
    "public BigDecimal computeMonthlyInterest(BigDecimal balance, BigDecimal rate) { return balance.multiply(rate).divide(BigDecimal.valueOf(1200), 2, RoundingMode.DOWN); }",
    "title: none | text: Ünïcödé — 1,234.56 € ✓  double  spaces\ttab and a « raw » byte",
    "task: classification | query: Equated monthly installment (EMI) calculation for declining-balance loans",
    "title: FNEM0400 · program | text: " + " ".join(["EMI of the related repayment periods (declining balance); FLAT reaches its tenant throw."] * 40),
]
tok = Tokenizer.from_file(model_dir + "/tokenizer.json")
doc = {"schema": "mlcpu.kb-embedding-fixture.v1",
       "generator": f"tokenizers {tokenizers.__version__}, onnxruntime {ort.__version__}, numpy {np.__version__}",
       "model": "onnx-community/embeddinggemma-300m-ONNX", "revision": "5090578d9565bb06545b4552f76e6bc2c93e4a66", "variants": {}}
for variant, file in [("fp32", "model.onnx"), ("q8", "model_quantized.onnx")]:
    sess = ort.InferenceSession(f"{model_dir}/onnx/{file}", providers=["CPUExecutionProvider"])
    items = []
    for t in texts:
        ids = tok.encode(t).ids[:2048]
        a = np.array([ids], dtype=np.int64)
        _, emb = sess.run(None, {"input_ids": a, "attention_mask": np.ones_like(a)})
        items.append({"text": t, "ids": ids, "vector": [round(float(x), 7) for x in emb[0]]})
    doc["variants"][variant] = items
with open(out, "w", encoding="utf-8", newline="\n") as f:
    json.dump(doc, f, ensure_ascii=False, separators=(",", ":"))
print(out, sum(len(v) for v in doc["variants"].values()), "vectors")
