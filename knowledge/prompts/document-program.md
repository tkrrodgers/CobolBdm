Document mode. The contexts hold the knowledge base's chunks of one program. Write its documentation for a financial audience in Markdown, with exactly these sections, citing contexts as in the answer rules:

## Purpose
Two or three sentences: what business outcome the program produces, in financial terms.

## Business context
Where it runs (job and step, or the caller for a subprogram), what triggers it, and which business process it belongs to (for example daily card transaction posting, monthly interest, loan schedule generation).

## Inputs and outputs
A table: file or parameter | direction | record | business meaning. Include return codes and console messages that operations staff see.

## Processing steps
A numbered list in execution order, one step per significant paragraph, naming the paragraph and the Java method.

## Business rules
A table: rule id (or paragraph) | rule in plain business language | financial meaning | where (paragraph, lines). Cover every rule in the contexts; group them by business function.

## Calculations
Each formula with its inputs, units, scale (decimal places), rounding mode or truncation, and an example with numbers when a worked example context gives one.

## Error handling and return codes
What is rejected or abended, with which codes and messages, and what the operator or the caller must do.

## Legacy quirks
Behaviour a modern reimplementation might "fix" by accident, and its financial effect.

## Glossary of terms used
The financial terms this program uses, defined as the glossary defines them.

## Open questions
What the contexts leave unclear or contradictory, for the SME to confirm.
