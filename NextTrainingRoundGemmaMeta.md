**Short answer: half of it.** Grounding Gemma from the database works and is measured. Training the weights on that data has not happened yet, so we can't yet say the full process works.

**What's proven:**
- **Grounding helps outside coding.** On the bar exam, Gemma went from 0.948 closed book to 0.981 with the curated library, about 3 points. On COBOL, validation went from 0.84 closed book to about 0.94 grounded.
- **The harvest pipeline works.** It turns verified practice answers into training data, with leak, license and quality checks.

**What's not proven:**
- **No weights have been trained.** There is no trained model to compare against base Gemma on the sealed final exam (gate G8). The only dataset is a draft that fails its own balance check.
- **"Pass rates go up as the process improves" is only partly learning.** Much of the rise came from fixing items, graders and retrieval. Learning-loop rounds 3, 4 and 6 helped and round 5 hurt.
- **The bar exam reduced the coding bias, but not cleanly.** Gemma was already at 0.948 without help. The teacher-written items look easier than the real exam (19/21 on the official sample). The 21 official items are too few, and Gemma has likely seen them in pretraining. The bar exam's quality gate (G3) was never passed.
- **On real coding tasks the gap is large.** Gemma passed 3 of 7 batch steps, and on the hard ones it copied the test's expected output instead of solving.

**Suggestions for coding-focused training:**

1. **Make executed tests the reward.** Every task needs many hidden scenarios with varied inputs, never just one. Keep the anti-copying check. Grade partial credit per scenario so near-misses still carry signal.
2. **Sample many attempts per task and keep the passing ones.** Today we keep one answer per item. Running 8–16 attempts per task and training on the successes, with pass-versus-fail pairs from the same task, is the cheapest reliable gain for code. Later, reinforcement learning on test-pass rewards can follow.
3. **Train on repair sequences, not only final answers.** The attempt loop already records "compiler or test feedback, then the fix". That skill transfers best to real work.
4. **Break large programs into smaller graded tasks.** Gemma fails at 60K–100K tokens of COBOL. Build subprogram and paragraph-level tasks with their own reference outputs, then whole steps, so training builds up to the big ones.
5. **Get variety from more generators, not more random seeds.** Several COBOL generators produce only a handful of distinct answers. The three topics with no generator at all need one.
6. **Split train and test by codebase.** Hold out whole estates and programs for validation and the sealed final. Seal a final task set before claiming any result.
7. **Filter training data for style, not just correctness.** Only harvest passing code that also meets the modern-Java rules, and keep the pipeline's intermediate transpiled Java out of the corpus.
8. **Train lightweight adapters (LoRA) on the 31B model.** Evaluate every checkpoint on three things: the sealed tasks, general coding benchmarks (to catch forgetting), and one non-coding domain such as the bar exam (to catch overfitting).
9. **Let training target what grounding can't fix.** Retrieval already supplies facts cheaply. Aim training at reasoning over code, tool use (reading large sources selectively), output format, and following feedback.
10. **Use larger samples and several seeds.** Results like 3 of 7 have very wide error bars. Expect noisy measurements until tasks number in the hundreds.